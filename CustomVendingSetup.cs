using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using UnityEngine;
using VLB;
using static ProtoBuf.VendingMachine;
using static VendingMachine;

namespace Oxide.Plugins
{
    [Info("Custom Vending Setup", "WhiteThunder", "2.2.0")]
    [Description("Allows editing orders at NPC vending machines.")]
    internal class CustomVendingSetup : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private Plugin MonumentFinder, VendingInStock;

        private static CustomVendingSetup _pluginInstance;
        private static SavedData _pluginData;

        private const string PermissionUse = "customvendingsetup.use";

        private const string StoragePrefab = "assets/prefabs/deployable/large wood storage/box.wooden.large.prefab";

        private const int ItemsPerRow = 6;

        // Going over 7 causes offers to get cut off regardless of resolution.
        private const int MaxVendingOffers = 7;

        private const int ShopNameNoteSlot = 29;
        private const int ContainerCapacity = 30;
        private const int MaxItemRows = ContainerCapacity / ItemsPerRow;

        private readonly object _boxedTrue = true;
        private readonly object _boxedFalse = false;

        private VendingMachineManager _vendingMachineManager = new VendingMachineManager();
        private VendingUIManager _vendingUIManager = new VendingUIManager();
        private ContainerUIManager _containerUIManager = new ContainerUIManager();

        private DynamicHookSubscriber<ulong> _uiViewers = new DynamicHookSubscriber<ulong>(
            nameof(OnLootEntityEnd)
        );

        private ItemDefinition _blueprintDefinition;
        private ItemDefinition _noteItemDefinition;
        private bool _serverInitialized = false;
        private bool _performingInstantRestock = false;

        #endregion

        #region Hooks

        private void Init()
        {
            _pluginInstance = this;
            _pluginData = SavedData.Load();

            permission.RegisterPermission(PermissionUse, this);

            _uiViewers.UnsubscribeAll();

            Unsubscribe(nameof(OnEntitySpawned));
        }

        private void OnServerInitialized()
        {
            if (CheckDependencies())
            {
                // Delay to allow Monument Finder to register monuments via its OnServerInitialized() hook.
                NextTick(() =>
                {
                    _vendingMachineManager.SetupAll();

                    foreach (var player in BasePlayer.activePlayerList)
                    {
                        var container = player.inventory.loot.containers.FirstOrDefault();
                        if (container == null)
                            continue;

                        var vendingMachine = container.entityOwner as NPCVendingMachine;
                        if (vendingMachine != null)
                            OnOpenVendingShop(vendingMachine, player);
                    }
                });
            }

            Subscribe(nameof(OnEntitySpawned));

            _blueprintDefinition = ItemManager.FindItemDefinition("blueprintbase");
            _noteItemDefinition = ItemManager.FindItemDefinition("note");
            _serverInitialized = true;
        }

        private void Unload()
        {
            _vendingUIManager.DestroyForAllPlayers();
            _containerUIManager.DestroyForAllPlayers();

            _vendingMachineManager.ResetAll();

            _pluginData = null;
            _pluginInstance = null;
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            // Check whether initialized to detect only late (re)loads.
            // Note: We are not dynamically subscribing to OnPluginLoaded since that interferes with [PluginReference] for some reason.
            if (_serverInitialized && plugin == MonumentFinder)
            {
                NextTick(_vendingMachineManager.SetupAll);
            }
        }

        private void OnEntitySpawned(NPCVendingMachine vendingMachine)
        {
            // Delay to give other plugins a chance to save a reference so they can block setup.
            NextTick(() =>
            {
                if (vendingMachine == null)
                    return;

                _vendingMachineManager.OnVendingMachineSpawned(vendingMachine);
            });
        }

        private void OnEntityKill(NPCVendingMachine vendingMachine)
        {
            _vendingMachineManager.OnVendingMachineKilled(vendingMachine);
        }

        private void OnOpenVendingShop(NPCVendingMachine vendingMachine, BasePlayer player)
        {
            var controller = _vendingMachineManager.GetController(vendingMachine);
            if (controller == null)
                return;

            if (!permission.UserHasPermission(player.UserIDString, PermissionUse))
                return;

            _vendingUIManager.ShowVendingUI(player, vendingMachine, controller.Profile);
            _uiViewers.Add(player.userID);
        }

        private void OnLootEntityEnd(BasePlayer player, StorageContainer storageContainer)
        {
            var vendingMachine = storageContainer as NPCVendingMachine;
            if (vendingMachine != null)
            {
                var controller = _vendingMachineManager.GetController(vendingMachine);
                if (controller == null)
                {
                    // Not at a monument.
                    return;
                }

                _vendingUIManager.DestroyForPlayer(player);
                _uiViewers.Remove(player.userID);
                return;
            }

            if (storageContainer.PrefabName == StoragePrefab)
            {
                var controller = _vendingMachineManager.GetControllerByContainer(storageContainer);
                if (controller == null)
                    return;

                _containerUIManager.DestroyForPlayer(player);
                _uiViewers.Remove(player.userID);
                controller.OnContainerClosed();

                return;
            }
        }

        private object OnVendingTransaction(NPCVendingMachine vendingMachine, BasePlayer player, int sellOrderIndex, int numberOfTransactions, ItemContainer targetContainer)
        {
            // Only override transaction logic if the vending machine is customized by this plugin.
            var vendingProfile = _vendingMachineManager.GetController(vendingMachine)?.Profile;
            if (vendingProfile?.Offers == null)
            {
                return null;
            }

            var offer = vendingProfile.GetOfferForSellOrderIndex(sellOrderIndex);
            if (offer == null)
            {
                return null;
            }

            // Get all item stacks in the vending machine that match the sold item.
            var sellableItems = Facepunch.Pool.GetList<Item>();
            var amountAvailable = 0;
            offer.SellItem.FindAllInContainer(vendingMachine.inventory, sellableItems, ref amountAvailable);
            if (sellableItems.Count == 0)
            {
                Facepunch.Pool.FreeList(ref sellableItems);
                return _boxedFalse;
            }

            // Verify the vending machine has sufficient stock.
            numberOfTransactions = Mathf.Clamp(numberOfTransactions, 1, sellableItems[0].hasCondition ? 1 : 1000000);
            var amountRequested = offer.SellItem.Amount * numberOfTransactions;
            if (amountRequested > amountAvailable)
            {
                Facepunch.Pool.FreeList(ref sellableItems);
                return _boxedFalse;
            }

            // Get all item stacks in the player inventory that match the currency item.
            var currencyItems = Facepunch.Pool.GetList<Item>();
            var currencyAvailable = 0;
            offer.CurrencyItem.FindAllInInventory(player.inventory, currencyItems, ref currencyAvailable);
            if (currencyItems.Count == 0)
            {
                Facepunch.Pool.FreeList(ref sellableItems);
                Facepunch.Pool.FreeList(ref currencyItems);
                return _boxedFalse;
            }

            // Verify the player has enough currency.
            var currencyRequired = offer.CurrencyItem.Amount * numberOfTransactions;
            if (currencyAvailable < currencyRequired)
            {
                Facepunch.Pool.FreeList(ref sellableItems);
                Facepunch.Pool.FreeList(ref currencyItems);
                return _boxedFalse;
            }

            var marketTerminal = targetContainer?.entityOwner as MarketTerminal;

            // Temporarily allow the vending machine internal storage to accept items.
            // Currency items are temporarily added to the vending machine internal storage before deleting.
            vendingMachine.transactionActive = true;

            var currencyToTake = currencyRequired;

            foreach (var currencyItem in currencyItems)
            {
                var amountToTake = Mathf.Min(currencyToTake, currencyItem.amount);
                currencyToTake -= amountToTake;

                var itemToTake = currencyItem.amount > amountToTake
                    ? SplitItem(currencyItem, amountToTake)
                    : currencyItem;

                vendingMachine.TakeCurrencyItem(itemToTake);
                marketTerminal?._onCurrencyRemovedCached?.Invoke(player, itemToTake);

                if (currencyToTake <= 0)
                {
                    break;
                }
            }

            vendingMachine.transactionActive = false;

            Facepunch.Pool.FreeList(ref currencyItems);

            // Perform instant restock if VendingInStock is loaded, since we are replacing its behavior.
            if (offer.RefillDelay == 0 || VendingInStock != null)
            {
                sellableItems[0].amount += amountRequested;
                _performingInstantRestock = true;
            }

            var maxStackSize = sellableItems[0].MaxStackable();
            var amountToGive = amountRequested;

            foreach (var sellableItem in sellableItems)
            {
                while (amountToGive > maxStackSize && sellableItem.amount > maxStackSize)
                {
                    var itemToGive1 = SplitItem(sellableItem, maxStackSize);
                    amountToGive -= itemToGive1.amount;

                    object canPurchaseHookResult1 = CallHookCanPurchaseItem(player, itemToGive1, marketTerminal?._onItemPurchasedCached, vendingMachine, targetContainer);
                    if (canPurchaseHookResult1 is bool)
                    {
                        Facepunch.Pool.FreeList(ref sellableItems);
                        return canPurchaseHookResult1;
                    }

                    GiveSoldItem(vendingMachine, itemToGive1, player, marketTerminal, targetContainer);
                }

                var itemToGive2 = sellableItem.amount > amountToGive
                    ? SplitItem(sellableItem, amountToGive)
                    : sellableItem;

                amountToGive -= itemToGive2.amount;

                object canPurchaseHookResult2 = CallHookCanPurchaseItem(player, itemToGive2, marketTerminal?._onItemPurchasedCached, vendingMachine, targetContainer);
                if (canPurchaseHookResult2 is bool)
                {
                    Facepunch.Pool.FreeList(ref sellableItems);
                    return canPurchaseHookResult2;
                }

                GiveSoldItem(vendingMachine, itemToGive2, player, marketTerminal, targetContainer);

                if (amountToGive <= 0)
                {
                    break;
                }
            }

            _performingInstantRestock = false;

            Facepunch.Pool.FreeList(ref sellableItems);
            vendingMachine.UpdateEmptyFlag();
            return _boxedTrue;
        }

        // This hook is exposed by plugin: Vending In Stock (VendingInStock).
        private object CanVendingStockRefill(NPCVendingMachine vendingMachine, Item soldItem, BasePlayer player)
        {
            // Prevent VendingInStock's refill logic while we are doing instant refill.
            // Why? So we can simply increase the stack size of an existing item, to avoid over filling the container with items.
            if (_performingInstantRestock)
            {
                return _boxedFalse;
            }

            return null;
        }

        #endregion

        #region API

        private bool API_IsCustomized(NPCVendingMachine vendingMachine)
        {
            var component = vendingMachine.GetComponent<VendingMachineComponent>();
            if (component == null)
                return false;

            return component.Profile?.Offers != null;
        }

        #endregion

        #region Dependencies

        private bool CheckDependencies()
        {
            if (MonumentFinder == null)
            {
                LogError("MonumentFinder is not loaded, get it at http://umod.org.");
                return false;
            }

            return true;
        }

        private class MonumentAdapter
        {
            public string PrefabName => (string)_monumentInfo["PrefabName"];
            public string Alias => (string)_monumentInfo["Alias"];
            public Vector3 Position => (Vector3)_monumentInfo["Position"];

            private Dictionary<string, object> _monumentInfo;

            public MonumentAdapter(Dictionary<string, object> monumentInfo)
            {
                _monumentInfo = monumentInfo;
            }

            public Vector3 InverseTransformPoint(Vector3 worldPosition) =>
                ((Func<Vector3, Vector3>)_monumentInfo["InverseTransformPoint"]).Invoke(worldPosition);

            public bool IsInBounds(Vector3 position) =>
                ((Func<Vector3, bool>)_monumentInfo["IsInBounds"]).Invoke(position);
        }

        private MonumentAdapter GetMonumentAdapter(Vector3 position)
        {
            var dictResult = MonumentFinder.Call("API_GetClosest", position) as Dictionary<string, object>;
            if (dictResult == null)
                return null;

            var monument = new MonumentAdapter(dictResult);
            return monument.IsInBounds(position) ? monument : null;
        }

        private MonumentAdapter GetMonumentAdapter(BaseEntity entity) =>
            GetMonumentAdapter(entity.transform.position);

        #endregion

        #region Exposed Hooks

        private static bool SetupVendingMachineWasBlocked(NPCVendingMachine vendingMachine)
        {
            object hookResult = Interface.CallHook("OnCustomVendingSetup", vendingMachine);
            return hookResult is bool && (bool)hookResult == false;
        }

        private static object CallHookCanPurchaseItem(BasePlayer player, Item item, Action<BasePlayer, Item> onItemPurchased, NPCVendingMachine vendingMachine, ItemContainer targetContainer)
        {
            return Interface.CallHook("CanPurchaseItem", player, item, onItemPurchased, vendingMachine, targetContainer);
        }

        #endregion

        #region Commands

        private static class UICommands
        {
            public const string Edit = "edit";
            public const string Reset = "reset";
            public const string Save = "save";
            public const string Cancel = "cancel";
            public const string ToggleBroadcast = "togglebroadcast";
        }

        [Command("customvendingsetup.ui")]
        private void CommandUI(IPlayer player, string cmd, string[] args)
        {
            if (args.Length < 2)
                return;

            NPCVendingMachine vendingMachine;
            VendingController controller;
            if (!PassesUICommandChecks(player, args, out vendingMachine, out controller))
                return;

            var basePlayer = player.Object as BasePlayer;
            var subCommand = args[1];

            switch (subCommand)
            {
                case UICommands.Edit:
                    if (controller.EditorPlayer != null)
                    {
                        ChatMessage(basePlayer, Lang.ErrorCurrentlyBeingEdited, controller.EditorPlayer.displayName);
                        basePlayer.EndLooting();
                        return;
                    }

                    controller.StartEditing(basePlayer, vendingMachine);
                    _containerUIManager.ShowContainerUI(basePlayer, vendingMachine, controller.EditFormState);
                    _uiViewers.Add(basePlayer.userID);
                    break;

                case UICommands.Reset:
                    controller.ResetProfile();
                    basePlayer.EndLooting();

                    OpenVendingMachineDelayed(basePlayer, vendingMachine);
                    break;

                case UICommands.ToggleBroadcast:
                    controller.EditFormState.Broadcast = !controller.EditFormState.Broadcast;
                    _containerUIManager.UpdateBroadcastUI(basePlayer, vendingMachine, controller.EditFormState);
                    break;

                case UICommands.Save:
                    controller.SaveUpdates(vendingMachine);
                    OpenVendingMachineDelayed(basePlayer, vendingMachine);
                    break;

                case UICommands.Cancel:
                    OpenVendingMachine(basePlayer, vendingMachine);
                    break;
            }
        }

        #endregion

        #region Helper Methods

        private static bool AreVectorsClose(Vector3 a, Vector3 b, float xZTolerance = 0.001f, float yTolerance = 10)
        {
            // Allow a generous amount of vertical distance given that plugins may snap entities to terrain.
            return Math.Abs(a.y - b.y) < yTolerance
                && Math.Abs(a.x - b.x) < xZTolerance
                && Math.Abs(a.z - b.z) < xZTolerance;
        }

        private bool PassesUICommandChecks(IPlayer player, string[] args, out NPCVendingMachine vendingMachine, out VendingController controller)
        {
            vendingMachine = null;
            controller = null;

            if (player.IsServer || !player.HasPermission(PermissionUse))
                return false;

            uint vendingMachineId;
            if (args.Length == 0 || !uint.TryParse(args[0], out vendingMachineId))
                return false;

            vendingMachine = BaseNetworkable.serverEntities.Find(vendingMachineId) as NPCVendingMachine;
            if (vendingMachine == null)
                return false;

            controller = _vendingMachineManager.GetController(vendingMachine);
            if (controller == null)
                return false;

            return true;
        }

        private void OpenVendingMachineDelayed(BasePlayer player, NPCVendingMachine vendingMachine, float delay = 0.25f)
        {
            timer.Once(delay, () =>
            {
                if (player == null || vendingMachine == null)
                    return;

                OpenVendingMachine(player, vendingMachine);
            });
        }

        private static Item CreateItem(ItemDefinition itemDefinition, int amount, string name, ulong skin, bool isBlueprint)
        {
            var item = ItemManager.Create(isBlueprint ? _pluginInstance._blueprintDefinition : itemDefinition, amount, skin);
            if (isBlueprint)
                item.blueprintTarget = itemDefinition.itemid;

            if (name != null)
                item.name = name;

            return item;
        }

        private static Item SplitItem(Item item, int amount)
        {
            var newItem = item.SplitItem(amount);
            newItem.name = item.name;
            return newItem;
        }

        private static void OpenVendingMachine(BasePlayer player, NPCVendingMachine vendingMachine)
        {
            if (vendingMachine.OccupiedCheck(player))
            {
                vendingMachine.SendSellOrders(player);
                vendingMachine.PlayerOpenLoot(player, vendingMachine.customerPanel);
                Interface.CallHook("OnOpenVendingShop", vendingMachine, player);
            }
        }

        private static VendingOffer[] GetOffersFromVendingMachine(NPCVendingMachine vendingMachine)
        {
            var vanillaOffers = vendingMachine.sellOrders.sellOrders;
            var offers = new VendingOffer[vanillaOffers.Count];

            for (var i = 0; i < offers.Length; i++)
            {
                var manifestEntry = vendingMachine.vendingOrders.orders.ElementAtOrDefault(i);
                offers[i] = VendingOffer.FromVanillaSellOrder(vanillaOffers[i], manifestEntry);
            }

            return offers;
        }

        private static VendingOffer[] GetOffersFromContainer(BasePlayer player, ItemContainer container)
        {
            var offers = new List<VendingOffer>();

            for (var columnIndex = 0; columnIndex < 2; columnIndex++)
            {
                for (var rowIndex = 0; rowIndex < MaxItemRows; rowIndex++)
                {
                    var sellItemSlot = rowIndex * ItemsPerRow + columnIndex * 3;

                    var sellItem = container.GetSlot(sellItemSlot);
                    var currencyItem = container.GetSlot(sellItemSlot + 1);
                    var settingsItem = container.GetSlot(sellItemSlot + 2);
                    if (sellItem == null || currencyItem == null)
                        continue;

                    offers.Add(VendingOffer.FromItems(player, sellItem, currencyItem, settingsItem));
                }
            }

            return offers.ToArray();
        }

        private static StorageContainer CreateContainerEntity(string prefabPath)
        {
            var entity = GameManager.server.CreateEntity(prefabPath);
            if (entity == null)
                return null;

            var container = entity as StorageContainer;
            if (container == null)
            {
                UnityEngine.Object.Destroy(entity);
                return null;
            }

            UnityEngine.Object.DestroyImmediate(container.GetComponent<DestroyOnGroundMissing>());
            UnityEngine.Object.DestroyImmediate(container.GetComponent<GroundWatch>());

            container.limitNetworking = true;
            container.EnableSaving(false);
            container.Spawn();

            return container;
        }

        private static int OrderIndexToSlot(int orderIndex)
        {
            if (orderIndex < MaxItemRows)
                return orderIndex * ItemsPerRow;

            return (orderIndex % MaxItemRows) * ItemsPerRow + 3;
        }

        private static StorageContainer CreateOrdersContainer(BasePlayer player, VendingOffer[] vendingOffers, string shopName)
        {
            var containerEntity = CreateContainerEntity(StoragePrefab);

            var container = containerEntity.inventory;
            container.allowedContents = ItemContainer.ContentsType.Generic;
            container.capacity = ContainerCapacity;

            for (var orderIndex = 0; orderIndex < vendingOffers.Length && orderIndex < 9; orderIndex++)
            {
                var offer = vendingOffers[orderIndex];
                var sellItem = offer.SellItem.Create();
                if (sellItem == null)
                    continue;

                var currencyItem = offer.CurrencyItem.Create();
                if (currencyItem == null)
                {
                    sellItem.Remove();
                    continue;
                }

                var destinationSlot = OrderIndexToSlot(orderIndex);

                if (!sellItem.MoveToContainer(container, destinationSlot))
                    sellItem.Remove();

                if (!currencyItem.MoveToContainer(container, destinationSlot + 1))
                    currencyItem.Remove();
            }

            // Add 7 note items, so the user doesn't have to make them.
            for (var orderIndex = 0; orderIndex < 7; orderIndex++)
            {
                var offer = vendingOffers.Length > orderIndex
                    ? vendingOffers[orderIndex]
                    : null;

                var settingsItem = ItemManager.Create(_pluginInstance._noteItemDefinition);
                if (settingsItem == null)
                    continue;

                var refillMaxLabel = _pluginInstance.GetMessage(player, Lang.SettingsRefillMax);
                var refillDelayLabel = _pluginInstance.GetMessage(player, Lang.SettingsRefillDelay);
                var refillAmountLabel = _pluginInstance.GetMessage(player, Lang.SettingsRefillAmount);

                settingsItem.text = $"{refillMaxLabel}: {offer?.RefillMax ?? VendingOffer.DefaultRefillMax}"
                    + $"\n{refillDelayLabel}: {offer?.RefillDelay ?? VendingOffer.DefaultRefillDelay}"
                    + $"\n{refillAmountLabel}: {offer?.RefillAmount ?? VendingOffer.DefaultRefillAmount}";

                var destinationSlot = OrderIndexToSlot(orderIndex);

                if (!settingsItem.MoveToContainer(container, destinationSlot + 2))
                    settingsItem.Remove();
            }

            var generalSettingsItem = ItemManager.Create(_pluginInstance._noteItemDefinition);
            if (generalSettingsItem != null)
            {
                generalSettingsItem.text = shopName;
                if (!generalSettingsItem.MoveToContainer(container, ShopNameNoteSlot))
                    generalSettingsItem.Remove();
            }

            return containerEntity;
        }

        private static void GiveSoldItem(NPCVendingMachine vendingMachine, Item item, BasePlayer player, MarketTerminal marketTerminal, ItemContainer targetContainer)
        {
            if (targetContainer == null)
            {
                vendingMachine.GiveSoldItem(item, player);
            }
            else if (!item.MoveToContainer(targetContainer))
            {
                item.Drop(targetContainer.dropPosition, targetContainer.dropVelocity);
            }

            marketTerminal?._onItemPurchasedCached?.Invoke(player, item);
        }

        #endregion

        #region UI

        private static class UIConstants
        {
            public const string EditButtonColor = "0.451 0.553 0.271 1";
            public const string EditButtonTextColor = "0.659 0.918 0.2 1";

            public const string ResetButtonColor = "0.9 0.5 0.2 1";
            public const string ResetButtonTextColor = "1 0.9 0.7 1";

            public const string SaveButtonColor = EditButtonColor;
            public const string SaveButtonTextColor = EditButtonTextColor;

            public const string CancelButtonColor = "0.4 0.4 0.4 1";
            public const string CancelButtonTextColor = "0.71 0.71 0.71 1";

            public const float PanelWidth = 380.5f;
            public const float HeaderHeight = 21;
            public const float ItemSpacing = 4;
            public const float ItemBoxSize = 58;

            public const int ButtonHorizontalSpacing = 6;

            public const int ButtonHeight = 32;
            public const int ButtonWidth = 80;

            public const string TexturedBackgroundSprite = "assets/content/ui/ui.background.tiletex.psd";
            public const string BroadcastIcon = "assets/icons/broadcast.png";
            public const string IconMaterial = "assets/icons/iconmaterial.mat";

            public const string AnchorMin = "0.5 0";
            public const string AnchorMax = "0.5 0";
        }

        private abstract class BaseUIManager
        {
            protected abstract string UIName { get; }

            protected HashSet<BasePlayer> _viewingPlayers = new HashSet<BasePlayer>();

            protected static void DestroyForPlayer(BasePlayer player, string uiName)
            {
                CuiHelper.DestroyUi(player, uiName);
            }

            public virtual void CreateUI(BasePlayer player, CuiElementContainer cuiElements)
            {
                _viewingPlayers.Add(player);
                CuiHelper.AddUi(player, cuiElements);
            }

            public virtual void DestroyForPlayer(BasePlayer player)
            {
                if (_viewingPlayers.Remove(player))
                    DestroyForPlayer(player, UIName);
            }

            public virtual void DestroyForAllPlayers()
            {
                foreach (var player in _viewingPlayers.ToArray())
                    DestroyForPlayer(player);
            }
        }

        private class VendingUIManager : BaseUIManager
        {
            protected override string UIName => "CustomVendingSetup.VendingUI";

            public void ShowVendingUI(BasePlayer player, NPCVendingMachine vendingMachine, VendingProfile profile)
            {
                DestroyForPlayer(player);

                var numSellOrders = vendingMachine.sellOrders?.sellOrders.Count ?? 0;
                var offsetY = 136 + 74 * numSellOrders;
                var offsetX = 192;

                var cuiElements = new CuiElementContainer
                {
                    {
                        new CuiPanel
                        {
                            RectTransform =
                            {
                                AnchorMin = UIConstants.AnchorMin,
                                AnchorMax = UIConstants.AnchorMax,
                                OffsetMin = $"{offsetX} {offsetY}",
                                OffsetMax = $"{offsetX} {offsetY}",
                            },
                        },
                        "Overlay",
                        UIName
                    }
                };

                var buttonIndex = 0;
                var vendingMachineId = vendingMachine.net.ID;

                if (profile != null)
                {
                    var resetButtonText = _pluginInstance.GetMessage(player, Lang.ButtonReset);
                    AddVendingButton(cuiElements, vendingMachineId, resetButtonText, UICommands.Reset, buttonIndex, UIConstants.ResetButtonColor, UIConstants.ResetButtonTextColor);
                    buttonIndex++;
                }

                var editButtonText = _pluginInstance.GetMessage(player, Lang.ButtonEdit);
                AddVendingButton(cuiElements, vendingMachineId, editButtonText, UICommands.Edit, buttonIndex, UIConstants.SaveButtonColor, UIConstants.SaveButtonTextColor);

                CreateUI(player, cuiElements);
            }

            private float GetButtonOffset(int reverseButtonIndex)
            {
                return UIConstants.PanelWidth - reverseButtonIndex * (UIConstants.ButtonWidth + UIConstants.ButtonHorizontalSpacing);
            }

            private void AddVendingButton(CuiElementContainer cuiElements, uint vendingMachineId, string text, string subCommand, int reverseButtonIndex, string color, string textColor)
            {
                var xMax = GetButtonOffset(reverseButtonIndex);
                var xMin = xMax - UIConstants.ButtonWidth;

                cuiElements.Add(
                    new CuiButton
                    {
                        Text =
                        {
                            Text = text,
                            Color = textColor,
                            Align = TextAnchor.MiddleCenter,
                            FontSize = 18,
                        },
                        Button =
                        {
                            Color = color,
                            FadeIn = 0.1f,
                            Command = $"customvendingsetup.ui {vendingMachineId} {subCommand}",
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "0 0",
                            OffsetMin = $"{xMin} 0",
                            OffsetMax = $"{xMax} {UIConstants.ButtonHeight}",
                        },
                    },
                    UIName
                );
            }
        }

        private class EditFormState
        {
            public static EditFormState FromVendingMachine(NPCVendingMachine vendingMachine)
            {
                return new EditFormState
                {
                    Broadcast = vendingMachine.IsBroadcasting(),
                };
            }

            public bool Broadcast;
        }

        private class ContainerUIManager : BaseUIManager
        {
            protected override string UIName => "CustomVendingSetup.ContainerUI";

            private const string TipUIName = "CustomVendingSetup.ContainerUI.Tip";
            private const string BroadcastUIName = "CustomVendingSetup.ContainerUI.Broadcast";

            public override void DestroyForAllPlayers()
            {
                // Stop looting the edit containers since they are going to be removed.
                foreach (var player in _viewingPlayers)
                    player.EndLooting();

                base.DestroyForAllPlayers();
            }

            public void ShowContainerUI(BasePlayer player, NPCVendingMachine vendingMachine, EditFormState uiState)
            {
                DestroyForPlayer(player);

                var offsetX = 192;
                var offsetY = 139;

                var cuiElements = new CuiElementContainer
                {
                    {
                        new CuiPanel
                        {
                            RectTransform =
                            {
                                AnchorMin = UIConstants.AnchorMin,
                                AnchorMax = UIConstants.AnchorMax,
                                OffsetMin = $"{offsetX} {offsetY + MaxItemRows * (UIConstants.ItemBoxSize + UIConstants.ItemSpacing)}",
                                OffsetMax = $"{offsetX} {offsetY + MaxItemRows * (UIConstants.ItemBoxSize + UIConstants.ItemSpacing)}",
                            },
                        },
                        "Overlay",
                        UIName
                    }
                };

                var saveButtonText = _pluginInstance.GetMessage(player, Lang.ButtonSave);
                var cancelButtonText = _pluginInstance.GetMessage(player, Lang.ButtonCancel);

                var vendingMachineId = vendingMachine.net.ID;

                AddButton(cuiElements, vendingMachineId, saveButtonText, UICommands.Save, 1, UIConstants.SaveButtonColor, UIConstants.SaveButtonTextColor);
                AddButton(cuiElements, vendingMachineId, cancelButtonText, UICommands.Cancel, 0, UIConstants.CancelButtonColor, UIConstants.CancelButtonTextColor);
                AddBroadcastButton(cuiElements, vendingMachine, uiState);

                var headerOffset = -6;

                cuiElements.Add(
                    new CuiElement
                    {
                        Components =
                        {
                            new CuiRawImageComponent
                            {
                                Color = UIConstants.CancelButtonColor,
                                Sprite = UIConstants.TexturedBackgroundSprite,
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = UIConstants.AnchorMin,
                                AnchorMax = UIConstants.AnchorMax,
                                OffsetMin = $"0 {headerOffset - UIConstants.HeaderHeight}",
                                OffsetMax = $"{UIConstants.PanelWidth} {headerOffset}",
                            }
                        },
                        Parent = UIName,
                        Name = TipUIName,
                    }
                );

                var forSaleText = _pluginInstance.GetMessage(player, Lang.InfoForSale);
                var costText = _pluginInstance.GetMessage(player, Lang.InfoCost);
                var settingsText = _pluginInstance.GetMessage(player, Lang.InfoSettings);

                AddHeaderLabel(cuiElements, 0, forSaleText);
                AddHeaderLabel(cuiElements, 1, costText);
                AddHeaderLabel(cuiElements, 2, settingsText);
                AddHeaderLabel(cuiElements, 3, forSaleText);
                AddHeaderLabel(cuiElements, 4, costText);
                AddHeaderLabel(cuiElements, 5, settingsText);

                CreateUI(player, cuiElements);
            }

            private void AddHeaderLabel(CuiElementContainer cuiElements, int index, string text)
            {
                float xMin = 6 + index * (UIConstants.ItemBoxSize + UIConstants.ItemSpacing);
                float xMax = xMin + UIConstants.ItemBoxSize;

                cuiElements.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = text,
                            Color = UIConstants.CancelButtonTextColor,
                            Align = TextAnchor.MiddleCenter,
                            FontSize = 13,
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "0 0",
                            OffsetMin = $"{xMin} 0",
                            OffsetMax = $"{xMax} {UIConstants.HeaderHeight}",
                        }
                    },
                    TipUIName
                );
            }

            private void AddBroadcastButton(CuiElementContainer cuiElements, NPCVendingMachine vendingMachine, EditFormState uiState)
            {
                var iconSize = UIConstants.ButtonHeight;

                var xMax = GetButtonOffset(2);
                var xMin = xMax - iconSize;

                cuiElements.Add(
                    new CuiElement
                    {
                        Components =
                        {
                            new CuiButtonComponent
                            {
                                Color = "0 0 0 0",
                                Command = $"customvendingsetup.ui {vendingMachine.net.ID} {UICommands.ToggleBroadcast}",
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "0 0",
                                OffsetMin = $"{xMin} 0",
                                OffsetMax = $"{xMax} {UIConstants.ButtonHeight}",
                            },
                        },
                        Parent = UIName,
                        Name = BroadcastUIName,
                    }
                );

                cuiElements.Add(
                    new CuiElement
                    {
                        Components =
                        {
                            new CuiRawImageComponent
                            {
                                Color = uiState.Broadcast ? UIConstants.SaveButtonTextColor : UIConstants.CancelButtonTextColor,
                                Sprite = UIConstants.BroadcastIcon,
                                Material = UIConstants.IconMaterial,
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "0 0",
                                OffsetMin = "0 0",
                                OffsetMax = $"{iconSize} {iconSize}",
                            },
                        },
                        Parent = BroadcastUIName,
                    }
                );
            }

            public void UpdateBroadcastUI(BasePlayer player, NPCVendingMachine vendingMachine, EditFormState uiState)
            {
                DestroyForPlayer(player, BroadcastUIName);

                var cuiElements = new CuiElementContainer();
                AddBroadcastButton(cuiElements, vendingMachine, uiState);
                CuiHelper.AddUi(player, cuiElements);
            }

            private float GetButtonOffset(int buttonIndex)
            {
                return UIConstants.PanelWidth - buttonIndex * (UIConstants.ButtonWidth + UIConstants.ButtonHorizontalSpacing);
            }

            private void AddButton(CuiElementContainer cuiElements, uint vendingMachineId, string text, string subCommand, int buttonIndex, string color, string textColor)
            {
                var xMax = GetButtonOffset(buttonIndex);
                var xMin = xMax - UIConstants.ButtonWidth;

                cuiElements.Add(
                    new CuiButton
                    {
                        Text =
                        {
                            Text = text,
                            Color = textColor,
                            Align = TextAnchor.MiddleCenter,
                            FontSize = 18,
                        },
                        Button =
                        {
                            Color = color,
                            FadeIn = 0.1f,
                            Command = $"customvendingsetup.ui {vendingMachineId} {subCommand}",
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "0 0",
                            OffsetMin = $"{xMin} 0",
                            OffsetMax = $"{xMax} {UIConstants.ButtonHeight}",
                        },
                    },
                    UIName
                );
            }
        }

        #endregion

        #region Utilities

        private interface IMonumentRelativePosition
        {
            string GetMonumentPrefabName();
            string GetMonumentAlias();
            Vector3 GetPosition();
            Vector3 GetLegacyPosition();
        }

        private static bool LocationsMatch(IMonumentRelativePosition a, IMonumentRelativePosition b)
        {
            var monumentsMatch = a.GetMonumentAlias() != null && a.GetMonumentAlias() == b.GetMonumentAlias()
                || a.GetMonumentPrefabName() == b.GetMonumentPrefabName();

            if (!monumentsMatch)
                return false;

            return AreVectorsClose(a.GetPosition(), b.GetPosition())
                || AreVectorsClose(a.GetLegacyPosition(), b.GetLegacyPosition());
        }

        private struct MonumentRelativePosition : IMonumentRelativePosition
        {
            public static MonumentRelativePosition? FromVendingMachine(NPCVendingMachine vendingMachine)
            {
                var monument = _pluginInstance.GetMonumentAdapter(vendingMachine);
                if (monument == null)
                    return null;

                return new MonumentRelativePosition
                {
                    _monument = monument,
                    _position = monument.InverseTransformPoint(vendingMachine.transform.position),
                    _legacyPosition = vendingMachine.transform.InverseTransformPoint(monument.Position),
                };
            }

            private MonumentAdapter _monument;
            private Vector3 _position;
            private Vector3 _legacyPosition;

            // IMonumentRelativePosition members.
            public string GetMonumentPrefabName() => _monument.PrefabName;
            public string GetMonumentAlias() => _monument.Alias;
            public Vector3 GetPosition() => _position;
            public Vector3 GetLegacyPosition() => _legacyPosition;
        }

        #endregion

        #region Vending Machine Manager

        private class VendingMachineManager
        {
            // At most one controller is present per monument-relative position.
            // For example, if there is a vending machine at each gas station at the same relative position, they use the same controller.
            private HashSet<VendingController> _controllers = new HashSet<VendingController>();

            // Controllers are also cached by vending machine, in case MonumentFinder is unloaded or becomes unstable.
            private Dictionary<uint, VendingController> _controllersByVendingMachine = new Dictionary<uint, VendingController>();

            public void OnVendingMachineSpawned(NPCVendingMachine vendingMachine)
            {
                var controller = GetController(vendingMachine);
                if (controller != null)
                {
                    // A controller may already exist if this was called when handling a reload or late load of MonumentFinder.
                    return;
                }

                var location = MonumentRelativePosition.FromVendingMachine(vendingMachine);
                if (location == null)
                {
                    // Not at a monument.
                    return;
                }

                if (SetupVendingMachineWasBlocked(vendingMachine))
                    return;

                controller = EnsureControllerForLocation(location.Value);
                controller.AddVendingMachine(vendingMachine);
                _controllersByVendingMachine[vendingMachine.net.ID] = controller;
            }

            public void OnVendingMachineKilled(NPCVendingMachine vendingMachine)
            {
                var controller = GetController(vendingMachine);
                if (controller == null)
                    return;

                controller.RemoveVendingMachine(vendingMachine);
            }

            public VendingController GetController(NPCVendingMachine vendingMachine)
            {
                VendingController controller;
                return _controllersByVendingMachine.TryGetValue(vendingMachine.net.ID, out controller)
                    ? controller
                    : null;
            }

            public VendingController GetControllerByContainer(StorageContainer container)
            {
                foreach (var controller in _controllersByVendingMachine.Values)
                {
                    if (controller.Container == container)
                        return controller;
                }

                return null;
            }

            public void SetupAll()
            {
                foreach (var entity in BaseNetworkable.serverEntities)
                {
                    var vendingMachine = entity as NPCVendingMachine;
                    if (vendingMachine == null)
                        continue;

                    OnVendingMachineSpawned(vendingMachine);
                }
            }

            public void ResetAll()
            {
                foreach (var controller in _controllersByVendingMachine.Values)
                    controller.ResetAll();
            }

            private VendingController GetControllerByLocation(MonumentRelativePosition location)
            {
                foreach (var controller in _controllers)
                {
                    if (LocationsMatch(controller.Location, location))
                        return controller;
                }

                return  null;
            }

            private VendingController EnsureControllerForLocation(MonumentRelativePosition location)
            {
                var controller = GetControllerByLocation(location);
                if (controller != null)
                    return controller;

                controller = new VendingController(location);
                _controllers.Add(controller);

                return controller;
            }
        }

        #endregion

        #region Vending Machine Controller

        private class VendingController
        {
            public MonumentRelativePosition Location { get; private set; }

            // While the Profile is null, the vending machines will be vanilla.
            public VendingProfile Profile { get; private set; }

            // These are temporary fields while the profile is being edited.
            public StorageContainer Container { get; private set; }
            public BasePlayer EditorPlayer { get; private set; }
            public EditFormState EditFormState { get; private set; }

            // List of vending machines with a position matching this controller.
            private HashSet<NPCVendingMachine> _vendingMachineList = new HashSet<NPCVendingMachine>();

            public VendingController(MonumentRelativePosition location)
            {
                Location = location;
                Profile = _pluginData.FindProfile(location);
            }

            public void ResetAll()
            {
                foreach (var vendingMachine in _vendingMachineList)
                    VendingMachineComponent.RemoveFromVendingMachine(vendingMachine);

                KillContainer();
            }

            public void SaveUpdates(NPCVendingMachine vendingMachine)
            {
                if (Profile == null)
                {
                    Profile = VendingProfile.FromVendingMachine(Location, vendingMachine);
                    _pluginData.VendingProfiles.Add(Profile);
                }

                Profile.Offers = GetOffersFromContainer(EditorPlayer, Container.inventory);
                Profile.Broadcast = EditFormState.Broadcast;

                var updatedShopName = Container.inventory.GetSlot(ShopNameNoteSlot)?.text.Trim();
                if (!string.IsNullOrEmpty(updatedShopName))
                    Profile.ShopName = updatedShopName;

                _pluginData.Save();

                SetupAll();
            }

            public void OnContainerClosed()
            {
                KillContainer();
                EditorPlayer = null;
                EditFormState = null;
            }

            public void ResetProfile()
            {
                _pluginData.VendingProfiles.Remove(Profile);
                Profile = null;
                ResetAll();
                _pluginData.Save();
            }

            public void StartEditing(BasePlayer player, NPCVendingMachine vendingMachine)
            {
                if (Container != null)
                    return;

                EditorPlayer = player;

                var offers = Profile?.Offers ?? GetOffersFromVendingMachine(vendingMachine);

                Container = CreateOrdersContainer(player, offers, vendingMachine.shopName);
                EditFormState = EditFormState.FromVendingMachine(vendingMachine);
                Container.SendAsSnapshot(player.Connection);
                Container.PlayerOpenLoot(player, Container.panelName, doPositionChecks: false);
            }

            public void AddVendingMachine(NPCVendingMachine vendingMachine)
            {
                if (!_vendingMachineList.Add(vendingMachine))
                    return;

                if (Profile != null)
                    VendingMachineComponent.AddToVendingMachine(vendingMachine, Profile);
            }

            public void RemoveVendingMachine(NPCVendingMachine vendingMachine)
            {
                if (!_vendingMachineList.Remove(vendingMachine))
                    return;

                if (Profile != null)
                    VendingMachineComponent.RemoveFromVendingMachine(vendingMachine);

                if (_vendingMachineList.Count == 0 && EditorPlayer != null)
                    EditorPlayer.EndLooting();
            }

            private void SetupAll()
            {
                foreach (var vendingMachine in _vendingMachineList)
                    VendingMachineComponent.AddToVendingMachine(vendingMachine, Profile);
            }

            private void KillContainer()
            {
                if (Container == null || Container.IsDestroyed)
                    return;

                if (EditorPlayer != null && EditorPlayer.IsConnected)
                    Container.OnNetworkSubscribersLeave(new List<Network.Connection> { EditorPlayer.Connection });

                Container.Kill();
                Container = null;
            }
        }

        #endregion

        #region Vending Machine Component

        private class VendingMachineComponent : EntityComponent<NPCVendingMachine>
        {
            public static void AddToVendingMachine(NPCVendingMachine vendingMachine, VendingProfile profile) =>
                vendingMachine.GetOrAddComponent<VendingMachineComponent>().AssignProfile(profile);

            public static void RemoveFromVendingMachine(NPCVendingMachine vendingMachine) =>
                DestroyImmediate(vendingMachine.GetComponent<VendingMachineComponent>());

            public VendingProfile Profile { get; private set; }
            private float[] _refillTimes;

            private string _originalShopName;
            private bool? _originalBroadcast;

            private void Awake()
            {
                baseEntity.CancelInvoke(baseEntity.InstallFromVendingOrders);
                baseEntity.CancelInvoke(baseEntity.Refill);

                InvokeRandomized(TimedRefill, 1, 1, 0.1f);
            }

            private void AssignProfile(VendingProfile profile)
            {
                Profile = profile;
                if (Profile?.Offers == null)
                    return;

                _refillTimes = new float[Profile.Offers.Length];

                var vendingMachine = baseEntity;

                for (var i = vendingMachine.inventory.itemList.Count - 1; i >= 0; i--)
                {
                    var item = vendingMachine.inventory.itemList[i];
                    item.RemoveFromContainer();
                    item.Remove();
                }

                vendingMachine.ClearSellOrders();

                if (_originalShopName == null)
                    _originalShopName = vendingMachine.shopName;

                if (_originalBroadcast == null)
                    _originalBroadcast = vendingMachine.IsBroadcasting();

                if (!string.IsNullOrEmpty(profile.ShopName))
                {
                    vendingMachine.shopName = profile.ShopName;
                }

                if (vendingMachine.IsBroadcasting() != profile.Broadcast)
                {
                    vendingMachine.SetFlag(VendingMachineFlags.Broadcasting, profile.Broadcast);
                    vendingMachine.UpdateMapMarker();
                }

                for (var i = 0; i < Profile.Offers.Length && i < MaxVendingOffers; i++)
                {
                    var offer = Profile.Offers[i];
                    if (!offer.IsValid)
                        continue;

                    var vendingOffer = new ProtoBuf.VendingMachine.SellOrder
                    {
                        ShouldPool = false,
                        itemToSellID = offer.SellItem.ItemId,
                        itemToSellAmount = offer.SellItem.Amount,
                        itemToSellIsBP = offer.SellItem.IsBlueprint,
                        currencyID = offer.CurrencyItem.ItemId,
                        currencyAmountPerItem = offer.CurrencyItem.Amount,
                        currencyIsBP = offer.CurrencyItem.IsBlueprint,
                    };

                    Interface.CallHook("OnAddVendingOffer", vendingMachine, vendingOffer);
                    vendingMachine.sellOrders.sellOrders.Add(vendingOffer);
                }

                CustomRefill(maxRefill: true);
            }

            private void ScheduleRefill(int offerIndex, VendingOffer offer, int min = 0)
            {
                _refillTimes[offerIndex] = Time.realtimeSinceStartup + Math.Max(offer.RefillDelay, min);
            }

            private void ScheduleDelayedRefill(int offerIndex, VendingOffer offer)
            {
                ScheduleRefill(offerIndex, offer, 300);
            }

            private void StopRefilling(int offerIndex)
            {
                _refillTimes[offerIndex] = float.MaxValue;
            }

            private void CustomRefill(bool maxRefill = false)
            {
                for (var offerIndex = 0; offerIndex < Profile.Offers.Length; offerIndex++)
                {
                    if (_refillTimes[offerIndex] > Time.realtimeSinceStartup)
                    {
                        continue;
                    }

                    var offer = Profile.Offers[offerIndex];
                    if (!offer.IsValid || offer.SellItem.Amount <= 0 || offer.CurrencyItem.Amount <= 0)
                    {
                        StopRefilling(offerIndex);
                        continue;
                    }

                    var totalAmountOfItem = offer.SellItem.GetAmountInContainer(baseEntity.inventory);
                    var numPurchasesInStock = totalAmountOfItem / offer.SellItem.Amount;
                    var refillNumberOfPurchases = offer.RefillMax - numPurchasesInStock;

                    if (!maxRefill)
                    {
                        refillNumberOfPurchases = Mathf.Min(refillNumberOfPurchases, offer.RefillAmount);
                    }

                    if (refillNumberOfPurchases <= 0)
                    {
                        ScheduleRefill(offerIndex, offer);
                        continue;
                    }

                    var refillAmount = 0;

                    try
                    {
                        refillAmount = checked(refillNumberOfPurchases * offer.SellItem.Amount);
                    }
                    catch (System.OverflowException ex)
                    {
                        _pluginInstance?.LogError($"Cannot multiply {refillNumberOfPurchases} by {offer.SellItem.Amount} because the result is too large. You have misconfigured the plugin. It is not necessary to stock that much of any item. Please reduce Max Stock or Refill Amount for item {offer.SellItem.ShortName}.\n" + ex.ToString());

                        // Prevent further refills to avoid spamming the console since this case cannot be fixed without editing the vending machine.
                        StopRefilling(offerIndex);
                        continue;
                    }

                    // Always increase the quantity of an existing item if present, rather than creating a new item.
                    // This is done to prevent ridiculous configurations from potentially filling up the vending machine with specific items.
                    var existingItem = offer.SellItem.FindInContainer(baseEntity.inventory);
                    if (existingItem != null)
                    {
                        try
                        {
                            existingItem.amount = checked(existingItem.amount + refillAmount);
                            existingItem.MarkDirty();
                            ScheduleRefill(offerIndex, offer);
                        }
                        catch (System.OverflowException ex)
                        {
                            _pluginInstance?.LogError($"Cannot add {refillAmount} to {existingItem.amount} because the result is too large. You have misconfigured the plugin. It is not necessary to stock that much of any item. Please reduce Max Stock or Refill Amount for item {offer.SellItem.ShortName}.\n" + ex.ToString());

                            // Reduce refill rate to avoid spamming the console.
                            ScheduleDelayedRefill(offerIndex, offer);
                        }
                        continue;
                    }

                    var item = offer.SellItem.Create(refillAmount);
                    if (item == null)
                    {
                        _pluginInstance?.LogError($"Unable to create item '{offer.SellItem.ShortName}'. Does that item exist? Was it removed from the game?");

                        // Prevent further refills to avoid spamming the console since this case cannot be fixed without editing the vending machine.
                        StopRefilling(offerIndex);
                        continue;
                    }

                    baseEntity.transactionActive = true;

                    if (item.MoveToContainer(baseEntity.inventory))
                    {
                        ScheduleRefill(offerIndex, offer);
                    }
                    else
                    {
                        _pluginInstance?.LogError($"Unable to add {item.amount} '{item.info.shortname}' because the vending machine container rejected it.");

                        item.Remove();

                        // Reduce refill rate to avoid spamming the console.
                        ScheduleDelayedRefill(offerIndex, offer);
                    }

                    baseEntity.transactionActive = false;
                }
            }

            private void TimedRefill() => CustomRefill();

            private void OnDestroy()
            {
                if (baseEntity == null || baseEntity.IsDestroyed)
                    return;

                if (_originalShopName != null)
                {
                    baseEntity.shopName = _originalShopName;
                }

                if (_originalBroadcast != null && _originalBroadcast != baseEntity.IsBroadcasting())
                {
                    baseEntity.SetFlag(VendingMachineFlags.Broadcasting, _originalBroadcast.Value);
                    baseEntity.UpdateMapMarker();
                }

                baseEntity.InstallFromVendingOrders();
                baseEntity.InvokeRandomized(baseEntity.Refill, 1f, 1f, 0.1f);
            }
        }

        #endregion

        #region Dynamic Hook Subscriptions

        private class DynamicHookSubscriber<T>
        {
            private HashSet<T> _list = new HashSet<T>();
            private string[] _hookNames;

            public DynamicHookSubscriber(params string[] hookNames)
            {
                _hookNames = hookNames;
            }

            public void Add(T item)
            {
                if (_list.Add(item) && _list.Count == 1)
                    SubscribeAll();
            }

            public void Remove(T item)
            {
                if (_list.Remove(item) && _list.Count == 0)
                    UnsubscribeAll();
            }

            public void SubscribeAll()
            {
                foreach (var hookName in _hookNames)
                    _pluginInstance.Subscribe(hookName);
            }

            public void UnsubscribeAll()
            {
                foreach (var hookName in _hookNames)
                    _pluginInstance.Unsubscribe(hookName);
            }
        }

        #endregion

        #region Legacy Saved Data

        private class LegacyVendingItem
        {
            public string Shortname = string.Empty;
            public string DisplayName = string.Empty;
            public int Amount = 1;
            public ulong Skin = 0;
            public bool IsBlueprint = false;
        }

        private class LegacyVendingOffer
        {
            public LegacyVendingItem Currency = new LegacyVendingItem();
            public LegacyVendingItem SellItem = new LegacyVendingItem();
        }

        private class LegacyVendingProfile
        {
            public string Id;
            public List<LegacyVendingOffer> Offers = new List<LegacyVendingOffer>();

            public string Shortname;
            public Vector3 WorldPosition;
            public Vector3 RelativePosition;
            public string RelativeMonument;

            public bool DetectByShortname = false;
        }

        #endregion

        #region Saved Data

        private class VendingItem
        {
            public static VendingItem FromItem(Item item)
            {
                var isBlueprint = item.IsBlueprint();
                var itemDefinition = isBlueprint
                    ? ItemManager.FindItemDefinition(item.blueprintTarget)
                    : item.info;

                return new VendingItem
                {
                    ShortName = itemDefinition.shortname,
                    Amount = item.amount,
                    DisplayName = item.name,
                    Skin = item.skin,
                    IsBlueprint = isBlueprint,
                };
            }

            [JsonProperty("ShortName")]
            public string ShortName;

            [JsonProperty("DisplayName", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string DisplayName;

            [JsonProperty("Amount")]
            public int Amount = 1;

            [JsonProperty("Skin", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public ulong Skin = 0;

            [JsonProperty("IsBlueprint", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool IsBlueprint = false;

            private ItemDefinition _itemDefinition;
            [JsonIgnore]
            public ItemDefinition Definition
            {
                get
                {
                    if (_itemDefinition == null && ShortName != null)
                        _itemDefinition = ItemManager.FindItemDefinition(ShortName);

                    return _itemDefinition;
                }
            }

            [JsonIgnore]
            public bool IsValid => Definition != null;

            [JsonIgnore]
            public int ItemId => Definition.itemid;

            public Item Create(int amount) =>
                IsValid ? CreateItem(Definition, amount, DisplayName, Skin, IsBlueprint) : null;

            public Item Create() => Create(Amount);

            public Item FindInContainer(ItemContainer container)
            {
                foreach (var item in container.itemList)
                {
                    if (DoesItemMatch(item))
                    {
                        return item;
                    }
                }

                return null;
            }

            public void FindAllInContainer(ItemContainer container, List<Item> resultItemList, ref int sum, float minCondition = 0)
            {
                foreach (var item in container.itemList)
                {
                    if (DoesItemMatch(item, minCondition))
                    {
                        resultItemList.Add(item);
                        sum += item.amount;
                    }
                }
            }

            public void FindAllInInventory(PlayerInventory playerInventory, List<Item> resultItemList, ref int sum)
            {
                FindAllInContainer(playerInventory.containerMain, resultItemList, ref sum, minCondition: 0.5f);
                FindAllInContainer(playerInventory.containerBelt, resultItemList, ref sum, minCondition: 0.5f);
                FindAllInContainer(playerInventory.containerWear, resultItemList, ref sum, minCondition: 0.5f);
            }

            public int GetAmountInContainer(ItemContainer container)
            {
                var count = 0;

                foreach (var item in container.itemList)
                {
                    if (DoesItemMatch(item))
                    {
                        count += item.amount;
                    }
                }

                return count;
            }

            private bool DoesItemMatch(Item item, float minCondition = 0)
            {
                if (IsBlueprint)
                {
                    return item.info == _pluginInstance?._blueprintDefinition && item.blueprintTarget == ItemId;
                }

                if (item.info.itemid != ItemId)
                {
                    return false;
                }

                if (item.skin != Skin)
                {
                    return false;
                }

                if ((item.name ?? string.Empty) != (DisplayName ?? string.Empty))
                {
                    return false;
                }

                if (minCondition > 0 && item.hasCondition && (item.conditionNormalized < minCondition || item.maxConditionNormalized < minCondition))
                {
                    return false;
                }

                return true;
            }
        }

        private class VendingOffer
        {
            public const int DefaultRefillMax = 10;
            public const int DefaultRefillDelay = 10;
            public const int DefaultRefillAmount = 1;

            public static VendingOffer FromVanillaSellOrder(SellOrder sellOrder, NPCVendingOrder.Entry manifestEntry)
            {
                return new VendingOffer
                {
                    SellItem = new VendingItem
                    {
                        ShortName = ItemManager.FindItemDefinition(sellOrder.itemToSellID)?.shortname,
                        Amount = sellOrder.itemToSellAmount,
                        IsBlueprint = sellOrder.itemToSellIsBP,
                    },
                    CurrencyItem = new VendingItem
                    {
                        ShortName = ItemManager.FindItemDefinition(sellOrder.currencyID)?.shortname,
                        Amount = sellOrder.currencyAmountPerItem,
                        IsBlueprint = sellOrder.currencyIsBP,
                    },
                    RefillDelay = manifestEntry != null ? (int)manifestEntry.refillDelay : DefaultRefillDelay,
                };
            }

            public static VendingOffer FromItems(BasePlayer player, Item sellItem, Item currencyItem, Item settingsItem)
            {
                var offer = new VendingOffer
                {
                    SellItem = VendingItem.FromItem(sellItem),
                    CurrencyItem = VendingItem.FromItem(currencyItem),
                };

                if (settingsItem != null)
                {
                    var refillMaxLabel = _pluginInstance.GetMessage(player, Lang.SettingsRefillMax);
                    var refillDelayLabel = _pluginInstance.GetMessage(player, Lang.SettingsRefillDelay);
                    var refillAmountLabel = _pluginInstance.GetMessage(player, Lang.SettingsRefillAmount);

                    var settings = ParseSettingsItem(settingsItem);
                    int refillMax, refillDelay, refillAmount;

                    if (settings.TryGetValue(refillMaxLabel, out refillMax))
                        offer.RefillMax = refillMax;

                    if (settings.TryGetValue(refillDelayLabel, out refillDelay))
                        offer.RefillDelay = refillDelay;

                    if (settings.TryGetValue(refillAmountLabel, out refillAmount))
                        offer.RefillAmount = refillAmount;
                }

                return offer;
            }

            private static Dictionary<string, int> ParseSettingsItem(Item settingsItem)
            {
                var dict = new Dictionary<string, int>();
                if (string.IsNullOrEmpty(settingsItem.text))
                    return dict;

                foreach (var line in settingsItem.text.Split('\n'))
                {
                    var parts = line.Split(':');
                    if (parts.Length < 2)
                        continue;

                    int value;
                    if (!int.TryParse(parts[1], out value))
                        continue;

                    dict[parts[0].Trim()] = value;
                }

                return dict;
            }

            [JsonProperty("SellItem")]
            public VendingItem SellItem;

            [JsonProperty("CurrencyItem")]
            public VendingItem CurrencyItem;

            [JsonProperty("RefillMax", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(DefaultRefillMax)]
            public int RefillMax = DefaultRefillMax;

            [JsonProperty("RefillDelay", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(DefaultRefillDelay)]
            public int RefillDelay = DefaultRefillDelay;

            [JsonProperty("RefillAmount", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(DefaultRefillAmount)]
            public int RefillAmount = DefaultRefillAmount;

            private SellOrder _sellOrder;
            [JsonIgnore]
            public SellOrder SellOrder
            {
                get
                {
                    if (_sellOrder == null)
                    {
                        _sellOrder = new SellOrder
                        {
                            ShouldPool = false,
                            itemToSellID = SellItem.Definition.itemid,
                            itemToSellAmount = SellItem.Amount,
                            itemToSellIsBP = SellItem.IsBlueprint,
                            currencyID = CurrencyItem.Definition.itemid,
                            currencyAmountPerItem = CurrencyItem.Amount,
                            currencyIsBP = CurrencyItem.IsBlueprint,
                        };
                    }

                    return _sellOrder;
                }
            }

            [JsonIgnore]
            public bool IsValid => SellItem.IsValid && CurrencyItem.IsValid;
        }

        private class VendingProfile : IMonumentRelativePosition
        {
            public static VendingProfile FromVendingMachine(MonumentRelativePosition location, NPCVendingMachine vendingMachine)
            {
                return new VendingProfile
                {
                    Monument = location.GetMonumentPrefabName(),
                    MonumentAlias = location.GetMonumentAlias(),
                    Position = location.GetPosition(),
                    ShopName = vendingMachine.shopName,
                    Broadcast = vendingMachine.IsBroadcasting(),
                    Offers = GetOffersFromVendingMachine(vendingMachine),
                };
            }

            [JsonProperty("ShopName", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string ShopName;

            [JsonProperty("Broadcast", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(true)]
            public bool Broadcast = true;

            [JsonProperty("Monument")]
            public string Monument;

            [JsonProperty("MonumentAlias", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string MonumentAlias;

            [JsonProperty("Position", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public Vector3 Position;

            [JsonProperty("LegacyPosition", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public Vector3 LegacyPosition;

            [JsonProperty("Offers")]
            public VendingOffer[] Offers;

            public VendingOffer GetOfferForSellOrderIndex(int index)
            {
                var sellOrderIndex = 0;

                for (var offerIndex = 0; offerIndex < Offers.Length; offerIndex++)
                {
                    var offer = Offers[offerIndex];
                    if (!offer.IsValid)
                        continue;

                    if (sellOrderIndex == index)
                        return offer;

                    sellOrderIndex++;
                }

                return null;
            }

            // IMonumentRelativePosition members.
            public string GetMonumentPrefabName() => Monument;
            public string GetMonumentAlias() => MonumentAlias;
            public Vector3 GetPosition() => Position;
            public Vector3 GetLegacyPosition() => LegacyPosition;
        }

        private class SavedData
        {
            // Legacy data for v1.
            [JsonProperty("Vendings", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public List<LegacyVendingProfile> Vendings;

            [JsonProperty("VendingProfiles")]
            public List<VendingProfile> VendingProfiles = new List<VendingProfile>();

            public static SavedData Load()
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<SavedData>(_pluginInstance.Name) ?? new SavedData();

                var dataMigrated = false;

                if (data.Vendings != null)
                {
                    foreach (var legacyProfile in data.Vendings)
                    {
                        var profile = new VendingProfile
                        {
                            Monument = legacyProfile.RelativeMonument,
                            LegacyPosition = legacyProfile.RelativePosition,
                            Offers = new VendingOffer[legacyProfile.Offers.Count],
                        };

                        for (var i = 0; i < legacyProfile.Offers.Count; i++)
                        {
                            var legacyOffer = legacyProfile.Offers[i];

                            profile.Offers[i] = new VendingOffer
                            {
                                SellItem = new VendingItem
                                {
                                    ShortName = legacyOffer.SellItem.Shortname,
                                    DisplayName = !string.IsNullOrEmpty(legacyOffer.SellItem.DisplayName) ? legacyOffer.SellItem.DisplayName : null,
                                    Amount = legacyOffer.SellItem.Amount,
                                    Skin = legacyOffer.SellItem.Skin,
                                    IsBlueprint = legacyOffer.SellItem.IsBlueprint,
                                },
                                CurrencyItem = new VendingItem
                                {
                                    ShortName = legacyOffer.Currency.Shortname,
                                    DisplayName = !string.IsNullOrEmpty(legacyOffer.Currency.DisplayName) ? legacyOffer.Currency.DisplayName : null,
                                    Amount = legacyOffer.Currency.Amount,
                                    Skin = legacyOffer.Currency.Skin,
                                    IsBlueprint = legacyOffer.Currency.IsBlueprint,
                                },
                            };
                        }

                        data.VendingProfiles.Add(profile);
                    }

                    dataMigrated = data.Vendings.Count > 0;
                    data.Vendings = null;
                    data.Save();
                    _pluginInstance.LogWarning($"Migrated data file to new format.");
                }

                return data;
            }

            public void Save() =>
                Interface.Oxide.DataFileSystem.WriteObject<SavedData>(_pluginInstance.Name, this);

            public VendingProfile FindProfile(MonumentRelativePosition location)
            {
                foreach (var profile in VendingProfiles)
                {
                    if (LocationsMatch(profile, location))
                    {
                        if (profile.LegacyPosition != Vector3.zero)
                        {
                            // Fix profile positioning.
                            profile.Position = location.GetPosition();
                            profile.LegacyPosition = Vector3.zero;
                        }
                        return profile;
                    }
                }

                return null;
            }
        }

        #endregion

        #region Localization

        private string GetMessage(string playerId, string messageName, params object[] args)
        {
            var message = lang.GetMessage(messageName, this, playerId);
            return args.Length > 0 ? string.Format(message, args) : message;
        }

        private string GetMessage(IPlayer player, string messageName, params object[] args) =>
            GetMessage(player.Id, messageName, args);

        private string GetMessage(BasePlayer player, string messageName, params object[] args) =>
            GetMessage(player.UserIDString, messageName, args);

        private void ReplyToPlayer(IPlayer player, string messageName, params object[] args) =>
            player.Reply(string.Format(GetMessage(player, messageName), args));

        private void ChatMessage(BasePlayer player, string messageName, params object[] args) =>
            player.ChatMessage(string.Format(GetMessage(player, messageName), args));

        private class Lang
        {
            public const string ButtonEdit = "Button.Edit";
            public const string ButtonReset = "Button.Reset";
            public const string InfoForSale = "Info.ForSale";
            public const string ButtonSave = "Button.Save";
            public const string ButtonCancel = "Button.Cancel";
            public const string InfoCost = "Info.Cost";
            public const string InfoSettings = "Info.Settings";
            public const string SettingsRefillMax = "Settings.RefillMax";
            public const string SettingsRefillDelay = "Settings.RefillDelay";
            public const string SettingsRefillAmount = "Settings.RefillAmount";
            public const string ErrorCurrentlyBeingEdited = "Error.CurrentlyBeingEdited";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.ButtonSave] = "SAVE",
                [Lang.ButtonCancel] = "CANCEL",
                [Lang.ButtonEdit] = "EDIT",
                [Lang.ButtonReset] = "RESET",
                [Lang.InfoForSale] = "FOR SALE",
                [Lang.InfoCost] = "COST",
                [Lang.InfoSettings] = "SETTINGS",
                [Lang.SettingsRefillMax] = "Max Stock",
                [Lang.SettingsRefillDelay] = "Seconds Between Refills",
                [Lang.SettingsRefillAmount] = "Refill Amount",
                [Lang.ErrorCurrentlyBeingEdited] = "That vending machine is currently being edited by {0}.",
            }, this, "en");
        }

        #endregion
    }
}
