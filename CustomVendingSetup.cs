using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using UnityEngine;
using VLB;
using static ProtoBuf.VendingMachine;
using static VendingMachine;

using CustomGetDataCallback = System.Func<Newtonsoft.Json.Linq.JObject>;
using CustomSaveDataCallback = System.Action<Newtonsoft.Json.Linq.JObject>;

namespace Oxide.Plugins
{
    [Info("Custom Vending Setup", "WhiteThunder", "2.8.0")]
    [Description("Allows editing orders at NPC vending machines.")]
    internal class CustomVendingSetup : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private Plugin BagOfHolding, InstantBuy, ItemRetriever, MonumentFinder;

        private SavedData _pluginData;
        private Configuration _pluginConfig;

        private const string PermissionUse = "customvendingsetup.use";

        private const string StoragePrefab = "assets/prefabs/deployable/large wood storage/box.wooden.large.prefab";

        private const int ItemsPerRow = 6;

        // Going over 7 causes offers to get cut off regardless of resolution.
        private const int MaxVendingOffers = 7;

        private const int ShopNameNoteSlot = 29;
        private const int ContainerCapacity = 30;
        private const int MaxItemRows = ContainerCapacity / ItemsPerRow;
        private const int BlueprintItemId = -996920608;
        private const float MinCurrencyCondition = 0.5f;

        private readonly object True = true;
        private readonly object False = false;

        private ItemRetrieverAdapter _itemRetrieverAdapter;
        private DataProviderRegistry _dataProviderRegistry = new DataProviderRegistry();
        private ComponentTracker<NPCVendingMachine, VendingMachineComponent> _componentTracker = new ComponentTracker<NPCVendingMachine, VendingMachineComponent>();
        private ComponentFactory<NPCVendingMachine, VendingMachineComponent> _componentFactory;
        private MonumentFinderAdapter _monumentFinderAdapter;
        private VendingMachineManager _vendingMachineManager;
        private BagOfHoldingLimitManager _bagOfHoldingLimitManager;
        private DynamicHookSubscriber<BaseVendingController> _inaccessibleVendingMachines;

        private ItemDefinition _noteItemDefinition;
        private bool _isServerInitialized;
        private bool _performingInstantRestock;
        private VendingItem _itemBeingSold;
        private Dictionary<string, object> _itemRetrieverQuery = new Dictionary<string, object>();
        private List<Item> _reusableItemList = new List<Item>();

        public CustomVendingSetup()
        {
            _monumentFinderAdapter = new MonumentFinderAdapter(this);
            _itemRetrieverAdapter = new ItemRetrieverAdapter(this);
            _componentFactory = new ComponentFactory<NPCVendingMachine, VendingMachineComponent>(this, _componentTracker);
            _vendingMachineManager = new VendingMachineManager(this, _componentFactory, _dataProviderRegistry);
            _bagOfHoldingLimitManager = new BagOfHoldingLimitManager(this);
            _inaccessibleVendingMachines = new DynamicHookSubscriber<BaseVendingController>(this, nameof(CanAccessVendingMachine));
        }

        #endregion

        #region Hooks

        private void Init()
        {
            _pluginConfig.Init();
            _pluginData = SavedData.Load();

            permission.RegisterPermission(PermissionUse, this);

            Unsubscribe(nameof(OnEntitySpawned));

            _inaccessibleVendingMachines.UnsubscribeAll();
        }

        private void OnServerInitialized()
        {
            _isServerInitialized = true;

            if (MonumentFinder == null)
            {
                LogError("MonumentFinder is not loaded, get it at http://umod.org.");
            }
            else
            {
                // Delay to allow Monument Finder to register monuments via its `OnServerInitialized()` hook.
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
                        {
                            OnVendingShopOpened(vendingMachine, player);
                        }
                    }
                });
            }

            if (ItemRetriever != null)
            {
                _itemRetrieverAdapter.HandleItemRetrieverLoaded();
            }

            _bagOfHoldingLimitManager.OnServerInitialized();

            Subscribe(nameof(OnEntitySpawned));

            _noteItemDefinition = ItemManager.FindItemDefinition("note");
        }

        private void Unload()
        {
            _vendingMachineManager.ResetAll();
            ObjectCache.Clear<int>();
            ObjectCache.Clear<float>();
            ObjectCache.Clear<ulong>();
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            switch (plugin.Name)
            {
                case nameof(MonumentFinder):
                {
                    // Check whether initialized to detect only late (re)loads.
                    // Note: We are not dynamically subscribing to OnPluginLoaded since that interferes with the PluginReference attribute.
                    if (_isServerInitialized)
                    {
                        // Delay to ensure MonumentFinder's `OnServerInitialized` method is called.
                        NextTick(_vendingMachineManager.SetupAll);
                    }
                    return;
                }

                case nameof(BagOfHolding):
                    _bagOfHoldingLimitManager.HandleBagOfHoldingLoadedChanged();
                    return;

                case nameof(ItemRetriever):
                    _itemRetrieverAdapter.HandleItemRetrieverLoaded();
                    return;
            }
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            switch (plugin.Name)
            {
                case nameof(ItemRetriever):
                    _itemRetrieverAdapter.HandleItemRetrieverUnloaded();
                    return;
            }
        }

        private void OnEntitySpawned(NPCVendingMachine vendingMachine)
        {
            // Delay to give other plugins a chance to save a reference so they can block setup.
            NextTick(() =>
            {
                if (vendingMachine == null || vendingMachine.IsDestroyed)
                    return;

                _vendingMachineManager.HandleVendingMachineSpawned(vendingMachine);
            });
        }

        private void OnEntityKill(NPCVendingMachine vendingMachine)
        {
            _vendingMachineManager.HandleVendingMachineKilled(vendingMachine);
        }

        private void OnVendingShopOpened(NPCVendingMachine vendingMachine, BasePlayer player)
        {
            var controller = _vendingMachineManager.GetController(vendingMachine);
            if (controller == null)
                return;

            var component = _componentTracker.GetComponent(vendingMachine);
            if (component == null)
                return;

            if (permission.UserHasPermission(player.UserIDString, PermissionUse))
            {
                component.ShowAdminUI(player);
            }

            if (_pluginConfig.ShopUISettings.EnableSkinOverlays && controller.Profile?.Offers != null)
            {
                component.ShowShopUI(player);
            }
        }

        private static bool HasCondition(ItemDefinition itemDefinition)
        {
            return itemDefinition.condition.enabled && itemDefinition.condition.max > 0;
        }

        private object OnVendingTransaction(NPCVendingMachine vendingMachine, BasePlayer player, int sellOrderIndex, int numberOfTransactions, ItemContainer targetContainer)
        {
            var vendingProfile = _vendingMachineManager.GetController(vendingMachine)?.Profile;
            if (vendingProfile?.Offers == null)
            {
                // Don't override the transaction logic because the vending machine is not customized by this plugin.
                return null;
            }

            var offer = vendingProfile.GetOfferForSellOrderIndex(sellOrderIndex);
            if (offer == null)
            {
                // Something is wrong. No valid offer exists at the specified index.
                return null;
            }

            numberOfTransactions = Mathf.Clamp(numberOfTransactions, 1, HasCondition(offer.SellItem.Definition) ? 1 : 1000000);

            var sellAmount = offer.SellItem.Amount * numberOfTransactions;
            var sellItemQuery = ItemQuery.FromSellItem(offer.SellItem);
            if (ItemUtils.SumContainerItems(vendingMachine.inventory, ref sellItemQuery) < sellAmount)
            {
                // The vending machine has insufficient stock.
                return False;
            }

            var currencyAmount = offer.CurrencyItem.Amount * numberOfTransactions;
            var currencyItemQuery = ItemQuery.FromCurrencyItem(offer.CurrencyItem);
            if (SumPlayerItems(player, ref currencyItemQuery) < currencyAmount)
            {
                // The player has insufficient currency.
                return False;
            }

            var marketTerminal = targetContainer?.entityOwner as MarketTerminal;
            var onMarketplaceItemPurchase = marketTerminal?._onItemPurchasedCached;

            _reusableItemList.Clear();
            TakePlayerItems(player, ref currencyItemQuery, currencyAmount, _reusableItemList);

            foreach (var currencyItem in _reusableItemList)
            {
                MaybeGiveWeaponAmmo(currencyItem, player);

                // Show a notice on the marketplace UI that the item was taken.
                onMarketplaceItemPurchase?.Invoke(player, currencyItem);

                // Instead of calling `vendingMachine.TakeCurrencyItem(itemToTake)`, just remove the item.
                // This fixes an "issue" where the item would go into the vending machine storage if there was a matching stack.
                // Note: The "OnTakeCurrencyItem" hook is not called because Item Retriever always takes the items.
                currencyItem.RemoveFromContainer();
                currencyItem.Remove();
            }

            _reusableItemList.Clear();

            var firstSellableItem = ItemUtils.FindFirstContainerItem(vendingMachine.inventory, ref sellItemQuery);
            var maxStackSize = _pluginConfig.GetItemMaxStackSize(firstSellableItem);

            if (offer.RefillDelay == 0)
            {
                // Don't change the stock amount. Instead, we will just leave the items in the vending machine.
                // The "CanVendingStockRefill" hook will use this flag to skip all logic.
                _performingInstantRestock = true;
            }
            else
            {
                ItemUtils.TakeContainerItems(vendingMachine.inventory, ref sellItemQuery, sellAmount);

                // The "CanVendingStockRefill" hook may use this to add stock.
                _itemBeingSold = offer.SellItem;
            }

            var totalAmountToGive = sellAmount;

            // Create new items and give them to the player.
            // This approach was chosen instead of transferring the items because in many cases new items would have to
            // be created anyway, since the vending machine maintains a single large stack of each item.
            while (totalAmountToGive > 0)
            {
                var amountToGive = Math.Min(totalAmountToGive, maxStackSize);
                var itemToGive = offer.SellItem.Create(amountToGive);

                totalAmountToGive -= amountToGive;

                // The "CanPurchaseItem" hook may cause "CanVendingStockRefill" hook to be called.
                var hookResult = ExposedHooks.CanPurchaseItem(player, itemToGive, onMarketplaceItemPurchase, vendingMachine, targetContainer);
                if (hookResult is bool)
                {
                    LogWarning($"A plugin returned {hookResult} in the CanPurchaseItem hook, which ${Name} has ignored.");
                }

                GiveSoldItem(vendingMachine, itemToGive, player, marketTerminal, targetContainer);
            }

            // These can now be unset since the "CanVendingStockRefill" hook can no longer be called after this point.
            _performingInstantRestock = false;
            _itemBeingSold = null;

            vendingMachine.UpdateEmptyFlag();

            if (InstantBuy == null)
            {
                var lootContainer = player.inventory.loot.containers.FirstOrDefault();
                if (lootContainer != null && lootContainer.entityOwner == vendingMachine)
                {
                    // Redraw the UI, since it would have been hidden in the "OnBuyVendingItem" hook.
                    OnVendingShopOpened(vendingMachine, player);
                }
            }

            if (offer.CustomSettings?.Count > 0)
            {
                ExposedHooks.OnCustomVendingSetupTransactionWithCustomSettings(vendingMachine, offer.CustomSettings);
            }

            return True;
        }

        private void OnBuyVendingItem(NPCVendingMachine vendingMachine, BasePlayer player, int sellOrderID, int amount)
        {
            if (!IsCustomized(vendingMachine))
                return;

            if (InstantBuy == null)
            {
                _componentTracker.GetComponent(vendingMachine)?.RemoveUI(player);
            }
        }

        // This hook is exposed by plugin: Vending In Stock (VendingInStock).
        private object CanVendingStockRefill(NPCVendingMachine vendingMachine, Item soldItem, BasePlayer player)
        {
            if (!IsCustomized(vendingMachine))
            {
                // Allow VendingInStock to restock the item.
                return null;
            }

            if (_performingInstantRestock)
            {
                // Don't restock the item, since it was never removed from the vending machine in the first place.
                return False;
            }

            // Override VendingInStock behavior to prevent creating new items in the container.
            // This also ensures additional item attributes are preserved.
            var itemQuery = ItemQuery.FromSellItem(_itemBeingSold);
            var existingItem = ItemUtils.FindFirstContainerItem(vendingMachine.inventory, ref itemQuery);
            if (existingItem != null)
            {
                existingItem.amount += soldItem.amount;
                existingItem.MarkDirty();
                return False;
            }

            if (_itemBeingSold == null)
            {
                // Something is wrong. The "CanPurchaseItem" hook was not called via this plugin.
                return null;
            }

            var newItem = _itemBeingSold.Create(soldItem.amount);
            vendingMachine.transactionActive = true;
            if (!newItem.MoveToContainer(vendingMachine.inventory, allowStack: false))
            {
                newItem.Remove();
            }
            vendingMachine.transactionActive = false;

            return False;
        }

        private object CanAccessVendingMachine(DeliveryDroneConfig deliveryDroneConfig, NPCVendingMachine vendingMachine)
        {
            if (!vendingMachine.IsBroadcasting())
                return null;

            var controller = _vendingMachineManager.GetController(vendingMachine);
            if (controller == null)
                return null;

            if (_inaccessibleVendingMachines.Contains(controller))
                return False;

            return null;
        }

        #endregion

        #region API

        private bool API_IsCustomized(NPCVendingMachine vendingMachine)
        {
            return IsCustomized(vendingMachine);
        }

        private void API_RefreshDataProvider(NPCVendingMachine vendingMachine)
        {
            _vendingMachineManager.HandleVendingMachineKilled(vendingMachine);
            _vendingMachineManager.HandleVendingMachineSpawned(vendingMachine);
        }

        // Undocumented. Intended for MonumentAddons migration to become a Data Provider.
        private JObject API_MigrateVendingProfile(NPCVendingMachine vendingMachine)
        {
            var location = MonumentRelativePosition.FromVendingMachine(_monumentFinderAdapter, vendingMachine);
            if (location == null)
            {
                // This can happen if a vending machine was moved outside a monument's bounds.
                return null;
            }

            var vendingProfile = _pluginData.FindProfile(location);
            if (vendingProfile == null)
            {
                return null;
            }

            JObject jObject;

            try
            {
                jObject = JObject.FromObject(vendingProfile);
            }
            catch (Exception e)
            {
                LogError($"Unable to migrate vending profile\n{e}");
                return null;
            }

            _pluginData.VendingProfiles.Remove(vendingProfile);
            _pluginData.Save();

            return jObject;
        }

        #endregion

        #region Dependencies

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

        private class MonumentFinderAdapter
        {
            private CustomVendingSetup _plugin;
            private Plugin _monumentFinder => _plugin.MonumentFinder;

            public MonumentFinderAdapter(CustomVendingSetup plugin)
            {
                _plugin = plugin;
            }

            public MonumentAdapter GetMonumentAdapter(Vector3 position)
            {
                var dictResult = _monumentFinder?.Call("API_GetClosest", position) as Dictionary<string, object>;
                if (dictResult == null)
                    return null;

                var monument = new MonumentAdapter(dictResult);
                return monument.IsInBounds(position) ? monument : null;
            }

            public MonumentAdapter GetMonumentAdapter(BaseEntity entity)
            {
                return GetMonumentAdapter(entity.transform.position);
            }
        }

        private class BagOfHoldingLimitManager
        {
            private class CustomLimitProfile
            {
                [JsonProperty("Max total bags")]
                public int MaxTotalBags = -1;
            }

            private CustomVendingSetup _plugin;
            private object _limitProfile;

            public BagOfHoldingLimitManager(CustomVendingSetup plugin)
            {
                _plugin = plugin;
            }

            public void OnServerInitialized()
            {
                HandleBagOfHoldingLoadedChanged();
            }

            public void HandleBagOfHoldingLoadedChanged()
            {
                if (_plugin.BagOfHolding == null)
                    return;

                _limitProfile = _plugin.BagOfHolding.Call("API_CreateLimitProfile", JsonConvert.SerializeObject(new CustomLimitProfile()));

                if (_limitProfile == null)
                {
                    LogError("Failed to create limit profile.");
                }
            }

            public void SetLimitProfile(ItemContainer container)
            {
                if (_limitProfile == null || _plugin.BagOfHolding == null)
                    return;

                var result = _plugin.BagOfHolding.Call("API_SetLimitProfile", container, _limitProfile);
                if (!(result is bool) || (bool)result == false)
                {
                    LogError("Failed to set limit profile for vending container");
                }
            }

            public void RemoveLimitProfile(ItemContainer container)
            {
                if (_limitProfile == null || _plugin.BagOfHolding == null)
                    return;

                _plugin.BagOfHolding.Call("API_RemoveLimitProfile", container);
            }
        }

        private class ItemRetrieverApi
        {
            public Func<BasePlayer, Dictionary<string, object>, int> SumPlayerItems { get; }
            public Func<BasePlayer, Dictionary<string, object>, int, List<Item>, int> TakePlayerItems { get; }

            public ItemRetrieverApi(Dictionary<string, object> apiDict)
            {
                SumPlayerItems = apiDict[nameof(SumPlayerItems)] as Func<BasePlayer, Dictionary<string, object>, int>;
                TakePlayerItems = apiDict[nameof(TakePlayerItems)] as Func<BasePlayer, Dictionary<string, object>, int, List<Item>, int>;
            }
        }

        private class ItemRetrieverAdapter
        {
            public ItemRetrieverApi Api { get; private set; }

            private CustomVendingSetup _plugin;

            private Plugin ItemRetriever => _plugin.ItemRetriever;

            public ItemRetrieverAdapter(CustomVendingSetup plugin)
            {
                _plugin = plugin;
            }

            public void HandleItemRetrieverLoaded()
            {
                Api = new ItemRetrieverApi(ItemRetriever.Call("API_GetApi") as Dictionary<string, object>);
            }

            public void HandleItemRetrieverUnloaded()
            {
                Api = null;
            }
        }

        #endregion

        #region Exposed Hooks

        private static class ExposedHooks
        {
            public static object OnCustomVendingSetup(NPCVendingMachine vendingMachine)
            {
                return Interface.CallHook("OnCustomVendingSetup", vendingMachine);
            }

            public static object CanPurchaseItem(BasePlayer player, Item item, Action<BasePlayer, Item> onItemPurchased, NPCVendingMachine vendingMachine, ItemContainer targetContainer)
            {
                return Interface.CallHook("CanPurchaseItem", player, item, onItemPurchased, vendingMachine, targetContainer);
            }

            public static Dictionary<string, object> OnCustomVendingSetupDataProvider(NPCVendingMachine vendingMachine)
            {
                return Interface.CallHook("OnCustomVendingSetupDataProvider", vendingMachine) as Dictionary<string, object>;
            }

            public static void OnCustomVendingSetupOfferSettingsParse(CaseInsensitiveDictionary<string> localizedSettings, CaseInsensitiveDictionary<object> customSettings)
            {
                Interface.CallHook("OnCustomVendingSetupOfferSettingsParse", localizedSettings, customSettings);
            }

            public static void OnCustomVendingSetupOfferSettingsDisplay(CaseInsensitiveDictionary<object> customSettings, CaseInsensitiveDictionary<string> localizedSettings)
            {
                Interface.CallHook("OnCustomVendingSetupOfferSettingsDisplay", customSettings, localizedSettings);
            }

            public static void OnCustomVendingSetupTransactionWithCustomSettings(NPCVendingMachine vendingMachine, CaseInsensitiveDictionary<object> customSettings)
            {
                Interface.CallHook("OnCustomVendingSetupTransactionWithCustomSettings", vendingMachine, customSettings);
            }
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
            public const string ToggleDroneAccessible = "toggledroneaccessible";
        }

        [Command("customvendingsetup.ui")]
        private void CommandUI(IPlayer player, string cmd, string[] args)
        {
            if (args.Length < 2)
                return;

            NPCVendingMachine vendingMachine;
            BaseVendingController vendingController;
            if (!PassesUICommandChecks(player, args, out vendingMachine, out vendingController))
                return;

            var basePlayer = player.Object as BasePlayer;
            var subCommand = args[1];

            switch (subCommand)
            {
                case UICommands.Edit:
                    if (vendingController.EditController != null)
                    {
                        basePlayer.EndLooting();
                        ChatMessage(basePlayer, Lang.ErrorCurrentlyBeingEdited, vendingController.EditController.EditorPlayer.displayName);
                        return;
                    }

                    vendingController.StartEditing(basePlayer, vendingMachine);
                    break;

                case UICommands.Reset:
                    vendingController.HandleReset();
                    vendingMachine.FullUpdate();
                    basePlayer.EndLooting();
                    basePlayer.inventory.loot.SendImmediate();
                    OpenVendingMachineDelayed(basePlayer, vendingMachine);
                    break;

                case UICommands.ToggleBroadcast:
                    vendingController.EditController?.ToggleBroadcast();
                    break;

                case UICommands.ToggleDroneAccessible:
                    vendingController.EditController?.ToggleDroneAccessible();
                    break;

                case UICommands.Save:
                    vendingController.HandleSave(vendingMachine);
                    vendingMachine.FullUpdate();
                    OpenVendingMachine(basePlayer, vendingMachine);
                    break;

                case UICommands.Cancel:
                    OpenVendingMachine(basePlayer, vendingMachine);
                    break;
            }
        }

        #endregion

        #region Helper Methods

        public static void LogInfo(string message) => Interface.Oxide.LogInfo($"[Custom Vending Setup] {message}");
        public static void LogError(string message) => Interface.Oxide.LogError($"[Custom Vending Setup] {message}");
        public static void LogWarning(string message) => Interface.Oxide.LogWarning($"[Custom Vending Setup] {message}");

        private static bool AreVectorsClose(Vector3 a, Vector3 b, float xZTolerance = 0.001f, float yTolerance = 10)
        {
            // Allow a generous amount of vertical distance given that plugins may snap entities to terrain.
            return Math.Abs(a.y - b.y) < yTolerance
                && Math.Abs(a.x - b.x) < xZTolerance
                && Math.Abs(a.z - b.z) < xZTolerance;
        }

        private static void OpenVendingMachine(BasePlayer player, NPCVendingMachine vendingMachine)
        {
            if (vendingMachine.OccupiedCheck(player) && Interface.CallHook("OnVendingShopOpen", vendingMachine, player) == null)
            {
                vendingMachine.SendSellOrders(player);
                vendingMachine.PlayerOpenLoot(player, vendingMachine.customerPanel);
                Interface.CallHook(nameof(OnVendingShopOpened), vendingMachine, player);
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

        private static VendingOffer[] GetOffersFromContainer(CustomVendingSetup plugin, BasePlayer player, ItemContainer container)
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

                    offers.Add(VendingOffer.FromItems(plugin, player, sellItem, currencyItem, settingsItem));
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

        private static string CreateNoteContents(Dictionary<string, string> settingsMap)
        {
            var lines = new List<string>();
            foreach (var entry in settingsMap)
            {
                lines.Add($"{entry.Key}: {entry.Value}");
            }
            return string.Join("\n", lines);
        }

        private static StorageContainer CreateOrdersContainer(CustomVendingSetup plugin, BasePlayer player, VendingOffer[] vendingOffers, string shopName)
        {
            var containerEntity = CreateContainerEntity(StoragePrefab);

            var container = containerEntity.inventory;
            container.allowedContents = ItemContainer.ContentsType.Generic;
            container.capacity = ContainerCapacity;

            plugin._bagOfHoldingLimitManager.SetLimitProfile(container);

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

                var settingsItem = ItemManager.Create(plugin._noteItemDefinition);
                if (settingsItem == null)
                    continue;

                var refillMaxLabel = plugin.GetMessage(player, Lang.SettingsRefillMax);
                var refillDelayLabel = plugin.GetMessage(player, Lang.SettingsRefillDelay);
                var refillAmountLabel = plugin.GetMessage(player, Lang.SettingsRefillAmount);

                var settingsMap = new CaseInsensitiveDictionary<string>
                {
                    [refillMaxLabel] = (offer?.RefillMax ?? VendingOffer.DefaultRefillMax).ToString(),
                    [refillDelayLabel] = (offer?.RefillDelay ?? VendingOffer.DefaultRefillDelay).ToString(),
                    [refillAmountLabel] = (offer?.RefillAmount ?? VendingOffer.DefaultRefillAmount).ToString(),
                };

                // Allow other plugins to parse the custom settings and display localized options.
                ExposedHooks.OnCustomVendingSetupOfferSettingsDisplay(
                    offer?.CustomSettings ?? new CaseInsensitiveDictionary<object>(), settingsMap);

                settingsItem.text = CreateNoteContents(settingsMap);

                var destinationSlot = OrderIndexToSlot(orderIndex);

                if (!settingsItem.MoveToContainer(container, destinationSlot + 2))
                    settingsItem.Remove();
            }

            var generalSettingsItem = ItemManager.Create(plugin._noteItemDefinition);
            if (generalSettingsItem != null)
            {
                generalSettingsItem.text = shopName;
                if (!generalSettingsItem.MoveToContainer(container, ShopNameNoteSlot))
                    generalSettingsItem.Remove();
            }

            return containerEntity;
        }

        private static void MaybeGiveWeaponAmmo(Item item, BasePlayer player)
        {
            var heldEntity = item.GetHeldEntity();
            if (heldEntity == null)
                return;

            if (heldEntity.creationFrame == Time.frameCount)
            {
                // The item was probably split off another item, so don't refund its ammo.
                return;
            }

            var baseProjectile = heldEntity as BaseProjectile;
            if ((object)baseProjectile != null)
            {
                var ammoType = baseProjectile.primaryMagazine?.ammoType;
                if (ammoType != null && baseProjectile.primaryMagazine.contents > 0)
                {
                    var ammoItem = ItemManager.Create(ammoType, baseProjectile.primaryMagazine.contents);
                    if (ammoItem != null)
                    {
                        player.GiveItem(ammoItem);
                    }
                }
                return;
            }

            var flameThrower = heldEntity as FlameThrower;
            if ((object)flameThrower != null)
            {
                if (flameThrower.fuelType != null && flameThrower.ammo > 0)
                {
                    var ammoItem = ItemManager.Create(flameThrower.fuelType, flameThrower.ammo);
                    if (ammoItem != null)
                    {
                        player.GiveItem(ammoItem);
                    }
                }
            }
        }

        private static void GiveSoldItem(NPCVendingMachine vendingMachine, Item item, BasePlayer player, MarketTerminal marketTerminal, ItemContainer targetContainer)
        {
            // Unset the placeholder flag to allow Enchanted Items to transform the artifact.
            item.SetFlag(Item.Flag.Placeholder, false);

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

        private Dictionary<string, object> SetupItemRetrieverQuery(ref ItemQuery itemQuery)
        {
            _itemRetrieverQuery.Clear();
            _itemRetrieverQuery["MinCondition"] = ObjectCache.Get(MinCurrencyCondition);
            _itemRetrieverQuery["RequireEmpty"] = True;

            if (itemQuery.BlueprintId != 0)
                _itemRetrieverQuery["BlueprintId"] = ObjectCache.Get(itemQuery.BlueprintId);

            if (itemQuery.DataInt != 0)
                _itemRetrieverQuery["DataInt"] = ObjectCache.Get(itemQuery.DataInt);

            if (itemQuery.ItemId != 0)
                _itemRetrieverQuery["ItemId"] = ObjectCache.Get(itemQuery.ItemId);

            if (itemQuery.SkinId.HasValue)
                _itemRetrieverQuery["SkinId"] = ObjectCache.Get(itemQuery.SkinId.Value);

            return _itemRetrieverQuery;
        }

        private int SumPlayerItems(BasePlayer player, ref ItemQuery itemQuery)
        {
            return _itemRetrieverAdapter?.Api?.SumPlayerItems.Invoke(player, SetupItemRetrieverQuery(ref itemQuery))
                   ?? ItemUtils.SumPlayerItems(player, ref itemQuery);
        }

        private int TakePlayerItems(BasePlayer player, ref ItemQuery itemQuery, int amount, List<Item> collect = null)
        {
            return _itemRetrieverAdapter?.Api?.TakePlayerItems.Invoke(player, SetupItemRetrieverQuery(ref itemQuery), amount, collect)
                   ?? ItemUtils.TakePlayerItems(player, ref itemQuery, amount, collect);
        }

        private bool PassesUICommandChecks(IPlayer player, string[] args, out NPCVendingMachine vendingMachine, out BaseVendingController controller)
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

        private void OpenVendingMachineDelayed(BasePlayer player, NPCVendingMachine vendingMachine, float delay = 0.1f)
        {
            timer.Once(delay, () =>
            {
                if (player == null || vendingMachine == null || vendingMachine.IsDestroyed)
                    return;

                OpenVendingMachine(player, vendingMachine);
            });
        }

        private bool IsCustomized(NPCVendingMachine vendingMachine) =>
            _vendingMachineManager.GetController(vendingMachine)?.Profile?.Offers != null;

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
            public const float HeaderHeight = 23;
            public const float ItemSpacing = 4;
            public const float ItemBoxSize = 58;

            public const int ButtonHorizontalSpacing = 6;

            public const int ButtonHeight = 32;
            public const int ButtonWidth = 80;

            public const string TexturedBackgroundSprite = "assets/content/ui/ui.background.tiletex.psd";
            public const string BroadcastIcon = "assets/icons/broadcast.png";
            public const string DroneIcon = "assets/icons/drone.png";
            public const string IconMaterial = "assets/icons/iconmaterial.mat";
            public const string GreyOutMaterial = "assets/icons/greyout.mat";

            public const string AnchorMin = "0.5 0";
            public const string AnchorMax = "0.5 0";
        }

        private class EditFormState
        {
            public static EditFormState FromVendingMachine(BaseVendingController vendingController, NPCVendingMachine vendingMachine)
            {
                return new EditFormState
                {
                    Broadcast = vendingController.Profile?.Broadcast ?? vendingMachine.IsBroadcasting(),
                    DroneAccessible = vendingController.Profile?.DroneAccessible ?? true,
                };
            }

            public bool Broadcast;
            public bool DroneAccessible;
        }

        private static class ContainerUIRenderer
        {
            public const string UIName = "CustomVendingSetup.ContainerUI";

            public const string TipUIName = "CustomVendingSetup.ContainerUI.Tip";
            public const string BroadcastUIName = "CustomVendingSetup.ContainerUI.Broadcast";
            public const string DroneUIName = "CustomVendingSetup.ContainerUI.Drone";

            public static string RenderContainerUI(CustomVendingSetup plugin, BasePlayer player, NPCVendingMachine vendingMachine, EditFormState uiState)
            {
                var offsetX = 192;
                var offsetY = 141;

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
                        "Hud.Menu",
                        UIName
                    }
                };

                var saveButtonText = plugin.GetMessage(player, Lang.ButtonSave);
                var cancelButtonText = plugin.GetMessage(player, Lang.ButtonCancel);

                var vendingMachineId = vendingMachine.net.ID;

                AddButton(
                    cuiElements,
                    vendingMachineId,
                    saveButtonText,
                    UICommands.Save,
                    UIConstants.PanelWidth - UIConstants.ButtonWidth - UIConstants.ButtonHorizontalSpacing,
                    UIConstants.SaveButtonColor,
                    UIConstants.SaveButtonTextColor
                );
                AddButton(
                    cuiElements,
                    vendingMachineId,
                    cancelButtonText,
                    UICommands.Cancel,
                    UIConstants.PanelWidth,
                    UIConstants.CancelButtonColor,
                    UIConstants.CancelButtonTextColor
                );
                AddBroadcastButton(cuiElements, vendingMachine, uiState);
                AddDroneButton(cuiElements, vendingMachine, uiState);

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

                var forSaleText = plugin.GetMessage(player, Lang.InfoForSale);
                var costText = plugin.GetMessage(player, Lang.InfoCost);
                var settingsText = plugin.GetMessage(player, Lang.InfoSettings);

                AddHeaderLabel(cuiElements, 0, forSaleText);
                AddHeaderLabel(cuiElements, 1, costText);
                AddHeaderLabel(cuiElements, 2, settingsText);
                AddHeaderLabel(cuiElements, 3, forSaleText);
                AddHeaderLabel(cuiElements, 4, costText);
                AddHeaderLabel(cuiElements, 5, settingsText);

                return CuiHelper.ToJson(cuiElements);
            }

            private static void AddHeaderLabel(CuiElementContainer cuiElements, int index, string text)
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

            private static void AddBroadcastButton(CuiElementContainer cuiElements, NPCVendingMachine vendingMachine, EditFormState uiState)
            {
                var iconSize = UIConstants.ButtonHeight;

                var xMax = UIConstants.PanelWidth - 2 * (UIConstants.ButtonWidth + UIConstants.ButtonHorizontalSpacing);
                var xMin = xMax - iconSize;

                cuiElements.Add(
                    new CuiElement
                    {
                        Parent = UIName,
                        Name = BroadcastUIName,
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
                    }
                );

                cuiElements.Add(
                    new CuiElement
                    {
                        Parent = BroadcastUIName,
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
                    }
                );
            }

            private static void AddDroneButton(CuiElementContainer cuiElements, NPCVendingMachine vendingMachine, EditFormState uiState)
            {
                var iconSize = UIConstants.ButtonHeight;

                var xMax = - UIConstants.ButtonHorizontalSpacing;
                var xMin = xMax - iconSize;

                var droneAccessible = uiState.Broadcast && uiState.DroneAccessible;

                cuiElements.Add(
                    new CuiElement
                    {
                        Parent = BroadcastUIName,
                        Name = DroneUIName,
                        Components =
                        {
                            new CuiButtonComponent
                            {
                                Color = "0 0 0 0",
                                Command = $"customvendingsetup.ui {vendingMachine.net.ID} {UICommands.ToggleDroneAccessible}",
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "0 0",
                                OffsetMin = $"{xMin} 0",
                                OffsetMax = $"{xMax} {UIConstants.ButtonHeight}",
                            },
                        },
                    }
                );

                cuiElements.Add(
                    new CuiElement
                    {
                        Parent = DroneUIName,
                        Components =
                        {
                            new CuiImageComponent
                            {
                                Color = droneAccessible ? UIConstants.SaveButtonTextColor : UIConstants.CancelButtonTextColor,
                                Sprite = UIConstants.DroneIcon,
                                Material = UIConstants.GreyOutMaterial,
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "0 0",
                                OffsetMin = "0 0",
                                OffsetMax = $"{iconSize} {iconSize}",
                            },
                        },
                    }
                );
            }

            public static string RenderBroadcastUI(BasePlayer player, NPCVendingMachine vendingMachine, EditFormState uiState)
            {
                var cuiElements = new CuiElementContainer();
                AddBroadcastButton(cuiElements, vendingMachine, uiState);
                AddDroneButton(cuiElements, vendingMachine, uiState);
                return CuiHelper.ToJson(cuiElements);
            }

            private static void AddButton(CuiElementContainer cuiElements, uint vendingMachineId, string text, string subCommand, float xMax, string color, string textColor)
            {
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

        private static class AdminUIRenderer
        {
            public const string UIName = "CustomVendingSetup.AdminUI";

            public static string RenderAdminUI(CustomVendingSetup plugin, BasePlayer player, NPCVendingMachine vendingMachine, VendingProfile profile)
            {
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
                    var resetButtonText = plugin.GetMessage(player, Lang.ButtonReset);
                    AddVendingButton(cuiElements, vendingMachineId, resetButtonText, UICommands.Reset, buttonIndex, UIConstants.ResetButtonColor, UIConstants.ResetButtonTextColor);
                    buttonIndex++;
                }

                var editButtonText = plugin.GetMessage(player, Lang.ButtonEdit);
                AddVendingButton(cuiElements, vendingMachineId, editButtonText, UICommands.Edit, buttonIndex, UIConstants.SaveButtonColor, UIConstants.SaveButtonTextColor);

                return CuiHelper.ToJson(cuiElements);
            }

            private static float GetButtonOffset(int reverseButtonIndex)
            {
                return UIConstants.PanelWidth - reverseButtonIndex * (UIConstants.ButtonWidth + UIConstants.ButtonHorizontalSpacing);
            }

            private static void AddVendingButton(CuiElementContainer cuiElements, uint vendingMachineId, string text, string subCommand, int reverseButtonIndex, string color, string textColor)
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

        private static class ShopUIRenderer
        {
            public const string UIName = "CustomVendingSetup.ShopUI";

            private const float OffsetXItem = 210;
            private const float OffsetXCurrency = 352;
            private const float OverlaySize = 60;

            private const float IconSize = 50;
            private const float PaddingLeft = 5.5f;
            private const float PaddingBottom = 8;

            public static string RenderShopUI(VendingProfile vendingProfile)
            {
                var cuiElements = new CuiElementContainer
                {
                    {
                        new CuiPanel
                        {
                            RectTransform =
                            {
                                AnchorMin = UIConstants.AnchorMin,
                                AnchorMax = UIConstants.AnchorMax,
                            },
                        },
                        "Hud.Menu",
                        UIName
                    }
                };

                var skinsByItemShortName = new Dictionary<string, HashSet<ulong>>();
                var numValidOffers = 0;

                foreach (var offer in vendingProfile.Offers)
                {
                    if (!offer.IsValid)
                        continue;

                    numValidOffers++;

                    HashSet<ulong> skins;
                    if (!skinsByItemShortName.TryGetValue(offer.SellItem.ShortName, out skins))
                    {
                        skins = new HashSet<ulong>();
                        skinsByItemShortName[offer.SellItem.ShortName] = skins;
                    }

                    skins.Add(offer.SellItem.SkinId);
                }

                var offerIndex = 0;

                foreach (var offer in vendingProfile.Offers)
                {
                    if (!offer.IsValid)
                        continue;

                    if (skinsByItemShortName[offer.SellItem.ShortName].Count > 1)
                    {
                        AddItemOverlay(cuiElements, numValidOffers - offerIndex, offer, isCurrency: false);
                    }

                    if (offer.CurrencyItem.SkinId != 0)
                    {
                        AddItemOverlay(cuiElements, numValidOffers - offerIndex, offer, isCurrency: true);
                    }

                    offerIndex++;
                }

                if (cuiElements.Count == 1)
                    return string.Empty;

                return CuiHelper.ToJson(cuiElements);
            }

            private static void AddItemOverlay(CuiElementContainer cuiElements, int indexFromBottom, VendingOffer offer, bool isCurrency = false)
            {
                var offsetX = isCurrency ? OffsetXCurrency : OffsetXItem;
                var offsetY = 40 + 74 * indexFromBottom;

                var vendingItem = isCurrency ? offer.CurrencyItem : offer.SellItem;

                // Background
                cuiElements.Add(
                    new CuiElement
                    {
                        Components =
                        {
                            new CuiRawImageComponent
                            {
                                Color = "0.35 0.35 0.35 1",
                                Sprite = UIConstants.TexturedBackgroundSprite,
                                FadeIn = 0.1f,
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = UIConstants.AnchorMin,
                                AnchorMax = UIConstants.AnchorMax,
                                OffsetMin = $"{offsetX} {offsetY}",
                                OffsetMax = $"{offsetX + OverlaySize} {offsetY + OverlaySize}",
                            }
                        },
                        Parent = UIName,
                    }
                );

                // Skin icon
                cuiElements.Add(new CuiElement
                {
                    Name = $"{UIName}.Offer.{indexFromBottom}.Currency",
                    Parent = UIName,
                    Components =
                    {
                        new CuiImageComponent
                        {
                            Sprite = "assets/content/textures/generic/fulltransparent.tga",
                            ItemId = vendingItem.ItemId,
                            SkinId = vendingItem.SkinId,
                            FadeIn = 0.1f,
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "0 0",
                            OffsetMin = $"{offsetX + PaddingLeft} {offsetY + PaddingBottom}",
                            OffsetMax = $"{offsetX + PaddingLeft + IconSize} {offsetY + PaddingBottom + IconSize}"
                        },
                    }
                });

                if (vendingItem.Amount > 1)
                {
                    // Amount
                    cuiElements.Add(
                        new CuiElement
                        {
                            Parent = UIName,
                            Components =
                            {
                                new CuiTextComponent
                                {
                                    Text = $"x{vendingItem.Amount}",
                                    Align = TextAnchor.LowerRight,
                                    FontSize = 12,
                                    Color = "0.65 0.65 0.65 1",
                                    FadeIn = 0.1f,
                                },
                                new CuiRectTransformComponent
                                {
                                    AnchorMin = UIConstants.AnchorMin,
                                    AnchorMax = UIConstants.AnchorMax,
                                    OffsetMin = $"{offsetX + 4} {offsetY + 1f}",
                                    OffsetMax = $"{offsetX - 3f + OverlaySize} {offsetY + OverlaySize}",
                                }
                            },
                        }
                    );
                }
            }
        }

        #endregion

        #region Utilities

        private static class StringUtils
        {
            public static bool Equals(string a, string b) =>
                string.Compare(a, b, StringComparison.OrdinalIgnoreCase) == 0;

            public static bool Contains(string haystack, string needle) =>
                haystack.Contains(needle, CompareOptions.IgnoreCase);
        }

        private static class ObjectCache
        {
            private static class StaticObjectCache<T>
            {
                private static readonly Dictionary<T, object> _cacheByValue = new Dictionary<T, object>();

                public static object Get(T value)
                {
                    object cachedObject;
                    if (!_cacheByValue.TryGetValue(value, out cachedObject))
                    {
                        cachedObject = value;
                        _cacheByValue[value] = cachedObject;
                    }
                    return cachedObject;
                }

                public static void Clear()
                {
                    _cacheByValue.Clear();
                }
            }

            public static object Get<T>(T value)
            {
                return StaticObjectCache<T>.Get(value);
            }

            public static void Clear<T>()
            {
                StaticObjectCache<T>.Clear();
            }
        }

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

        private class MonumentRelativePosition : IMonumentRelativePosition
        {
            public static MonumentRelativePosition FromVendingMachine(MonumentFinderAdapter monumentFinderAdapter, NPCVendingMachine vendingMachine)
            {
                var monument = monumentFinderAdapter.GetMonumentAdapter(vendingMachine);
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

        #region Item Query

        private struct ItemQuery
        {
            public static ItemQuery FromSellItem(VendingItem vendingItem)
            {
                return new ItemQuery
                {
                    BlueprintId = vendingItem.IsBlueprint ? vendingItem.ItemId : 0,
                    DataInt = vendingItem.DataInt,
                    DisplayName = vendingItem.DisplayName,
                    ItemId = vendingItem.IsBlueprint ? BlueprintItemId : vendingItem.ItemId,
                    SkinId = vendingItem.SkinId,
                };
            }

            public static ItemQuery FromCurrencyItem(VendingItem vendingItem)
            {
                var itemQuery = new ItemQuery
                {
                    BlueprintId = vendingItem.IsBlueprint ? vendingItem.ItemId : 0,
                    MinCondition = MinCurrencyCondition,
                    ItemId = vendingItem.IsBlueprint ? BlueprintItemId : vendingItem.ItemId,
                };

                if (vendingItem.SkinId != 0)
                {
                    itemQuery.SkinId = vendingItem.SkinId;
                }

                return itemQuery;
            }

            public int BlueprintId;
            public int DataInt;
            public string DisplayName;
            public Item.Flag Flags;
            public int ItemId;
            public float MinCondition;
            public bool RequireEmpty;
            public ulong? SkinId;

            public int GetUsableAmount(Item item)
            {
                if (ItemId != 0 && ItemId != item.info.itemid)
                    return 0;

                if (SkinId.HasValue && SkinId != item.skin)
                    return 0;

                if (BlueprintId != 0 && BlueprintId != item.blueprintTarget)
                    return 0;

                if (DataInt != 0 && DataInt != (item.instanceData?.dataInt ?? 0))
                    return 0;

                if (Flags != 0 && !item.flags.HasFlag(Flags))
                    return 0;

                if (MinCondition > 0 && item.hasCondition && (item.conditionNormalized < MinCondition || item.maxConditionNormalized < MinCondition))
                    return 0;

                if (!string.IsNullOrEmpty(DisplayName) && !StringUtils.Equals(DisplayName, item.name))
                    return 0;

                return RequireEmpty && item.contents?.itemList?.Count > 0
                    ? Math.Max(0, item.amount - 1)
                    : item.amount;
            }
        }

        #endregion

        #region Item Utils

        private static class ItemUtils
        {
            public static Item FindFirstContainerItem(ItemContainer container, ref ItemQuery itemQuery)
            {
                foreach (var item in container.itemList)
                {
                    if (itemQuery.GetUsableAmount(item) > 0)
                        return item;
                }

                return null;
            }

            public static int SumContainerItems(ItemContainer container, ref ItemQuery itemQuery)
            {
                var sum = 0;

                foreach (var item in container.itemList)
                {
                    sum += itemQuery.GetUsableAmount(item);
                }

                return sum;
            }

            public static void FindContainerItems(ItemContainer container, ref ItemQuery itemQuery, List<Item> collect)
            {
                foreach (var item in container.itemList)
                {
                    if (itemQuery.GetUsableAmount(item) <= 0)
                        continue;

                    collect.Add(item);
                }
            }

            public static int SumPlayerItems(BasePlayer player, ref ItemQuery itemQuery)
            {
                return SumContainerItems(player.inventory.containerMain, ref itemQuery)
                    + SumContainerItems(player.inventory.containerBelt, ref itemQuery);
            }

            public static int TakeContainerItems(ItemContainer container, ref ItemQuery itemQuery, int totalAmountToTake, List<Item> collect = null)
            {
                var totalAmountTaken = 0;

                for (var i = container.itemList.Count - 1; i >= 0; i--)
                {
                    var amountToTake = totalAmountToTake - totalAmountTaken;
                    if (amountToTake <= 0)
                        break;

                    var item = container.itemList[i];
                    var usableAmount = itemQuery.GetUsableAmount(item);
                    if (usableAmount > 0)
                    {
                        amountToTake = Math.Min(item.amount, amountToTake);

                        if (item.amount > amountToTake)
                        {
                            if (collect != null)
                            {
                                var splitItem = item.SplitItem(amountToTake);
                                var playerOwner = splitItem.GetOwnerPlayer();
                                if (playerOwner != null)
                                {
                                    splitItem.CollectedForCrafting(playerOwner);
                                }
                                collect.Add(splitItem);
                            }
                            else
                            {
                                item.amount -= amountToTake;
                                item.MarkDirty();
                            }
                        }
                        else
                        {
                            item.RemoveFromContainer();

                            if (collect != null)
                            {
                                collect.Add(item);
                            }
                            else
                            {
                                item.Remove();
                            }
                        }

                        totalAmountTaken += amountToTake;
                    }

                    if (totalAmountTaken >= totalAmountToTake)
                        return totalAmountTaken;
                }

                return totalAmountTaken;
            }

            public static int TakePlayerItems(BasePlayer player, ref ItemQuery itemQuery, int amountToTake, List<Item> collect = null)
            {
                var amountTaken = TakeContainerItems(player.inventory.containerMain, ref itemQuery, amountToTake, collect);
                if (amountTaken >= amountToTake)
                    return amountTaken;

                amountTaken += TakeContainerItems(player.inventory.containerBelt, ref itemQuery, amountToTake - amountTaken, collect);
                if (amountTaken >= amountToTake)
                    return amountTaken;

                amountTaken += TakeContainerItems(player.inventory.containerWear, ref itemQuery, amountToTake - amountTaken, collect);
                if (amountTaken >= amountToTake)
                    return amountTaken;

                return amountTaken;
            }
        }

        #endregion

        #region Dynamic Hook Subscriptions

        private class DynamicHookSubscriber<T>
        {
            private CustomVendingSetup _plugin;
            private HashSet<T> _list = new HashSet<T>();
            private string[] _hookNames;

            public DynamicHookSubscriber(CustomVendingSetup plugin, params string[] hookNames)
            {
                _plugin = plugin;
                _hookNames = hookNames;
            }

            public bool Contains(T item)
            {
                return _list.Contains(item);
            }

            public void Add(T item)
            {
                if (_list.Add(item) && _list.Count == 1)
                {
                    SubscribeAll();
                }
            }

            public void Remove(T item)
            {
                if (_list.Remove(item) && _list.Count == 0)
                {
                    UnsubscribeAll();
                }
            }

            public void SubscribeAll()
            {
                foreach (var hookName in _hookNames)
                {
                    _plugin.Subscribe(hookName);
                }
            }

            public void UnsubscribeAll()
            {
                foreach (var hookName in _hookNames)
                {
                    _plugin.Unsubscribe(hookName);
                }
            }
        }

        #endregion

        #region Data Provider

        private class DataProvider
        {
            public static DataProvider FromDictionary(Dictionary<string, object> spec)
            {
                var dataProvider = new DataProvider
                {
                    Spec = spec,
                };

                object getDataCallback, saveDataCallback;

                if (spec.TryGetValue("GetData", out getDataCallback))
                {
                    dataProvider.GetDataCallback = getDataCallback as CustomGetDataCallback;
                }

                if (spec.TryGetValue("SaveData", out saveDataCallback))
                {
                    dataProvider.SaveDataCallback = saveDataCallback as CustomSaveDataCallback;
                }

                if (dataProvider.GetDataCallback == null)
                {
                    LogError("Data provider missing GetData");
                    return null;
                }

                if (dataProvider.SaveDataCallback == null)
                {
                    LogError("Data provider missing SaveData");
                    return null;
                }

                return dataProvider;
            }

            public Dictionary<string, object> Spec { get; private set; }
            public CustomGetDataCallback GetDataCallback;
            public CustomSaveDataCallback SaveDataCallback;

            private VendingProfile _vendingProfile;
            private JObject _serializedData;

            public VendingProfile GetData()
            {
                if (_vendingProfile == null)
                {
                    _vendingProfile = GetDataCallback()?.ToObject<VendingProfile>();
                }

                if (_vendingProfile?.Offers == null)
                {
                    return null;
                }

                return _vendingProfile;
            }

            public void SaveData(VendingProfile vendingProfile)
            {
                _vendingProfile = vendingProfile;
                SaveDataCallback(vendingProfile != null ? JObject.FromObject(vendingProfile) : null);
            }
        }

        private class DataProviderRegistry
        {
            private Dictionary<Dictionary<string, object>, DataProvider> _dataProviderCache = new Dictionary<Dictionary<string, object>, DataProvider>();

            public DataProvider Register(Dictionary<string, object> dataProviderSpec)
            {
                DataProvider dataProvider;
                if (!_dataProviderCache.TryGetValue(dataProviderSpec, out dataProvider))
                {
                    dataProvider = DataProvider.FromDictionary(dataProviderSpec);
                    if (dataProvider == null)
                    {
                        return null;
                    }

                    _dataProviderCache[dataProviderSpec] = dataProvider;
                    return dataProvider;
                }

                return dataProvider;
            }

            public void Unregister(DataProvider dataProvider)
            {
                _dataProviderCache.Remove(dataProvider.Spec);
            }
        }

        #endregion

        #region Vending Machine Manager

        private class VendingMachineManager
        {
            private CustomVendingSetup _plugin;
            private ComponentFactory<NPCVendingMachine, VendingMachineComponent> _componentFactory;
            private DataProviderRegistry _dataProviderRegistry;

            private HashSet<BaseVendingController> _uniqueControllers = new HashSet<BaseVendingController>();

            // Controllers are also cached by vending machine, in case MonumentFinder is unloaded or becomes unstable.
            private Dictionary<uint, BaseVendingController> _controllersByVendingMachine = new Dictionary<uint, BaseVendingController>();

            private Dictionary<DataProvider, CustomVendingController> _controllersByDataProvider = new Dictionary<DataProvider, CustomVendingController>();

            public VendingMachineManager(CustomVendingSetup plugin, ComponentFactory<NPCVendingMachine, VendingMachineComponent> componentFactory, DataProviderRegistry dataProviderRegistry)
            {
                _plugin = plugin;
                _componentFactory = componentFactory;
                _dataProviderRegistry = dataProviderRegistry;
            }

            public void HandleVendingMachineSpawned(NPCVendingMachine vendingMachine)
            {
                if (GetController(vendingMachine) != null)
                {
                    // A controller may already exist if this was called when handling a reload of MonumentFinder.
                    return;
                }

                BaseVendingController controller = null;

                var dataProviderSpec = ExposedHooks.OnCustomVendingSetupDataProvider(vendingMachine);
                if (dataProviderSpec != null)
                {
                    var dataProvider = _dataProviderRegistry.Register(dataProviderSpec);
                    if (dataProvider == null)
                    {
                        // Data provider is invalid.
                        return;
                    }

                    var hookResult = ExposedHooks.OnCustomVendingSetup(vendingMachine);
                    if (hookResult is bool && !(bool)hookResult)
                        return;

                    controller = EnsureCustomController(dataProvider);
                }
                else
                {
                    var location = MonumentRelativePosition.FromVendingMachine(_plugin._monumentFinderAdapter, vendingMachine);
                    if (location == null)
                    {
                        // Not at a monument.
                        return;
                    }

                    var hookResult = ExposedHooks.OnCustomVendingSetup(vendingMachine);
                    if (hookResult is bool && !(bool)hookResult)
                        return;

                    controller = EnsureMonumentController(location);
                }

                controller.AddVendingMachine(vendingMachine);
                _controllersByVendingMachine[vendingMachine.net.ID] = controller;
            }

            public void HandleVendingMachineKilled(NPCVendingMachine vendingMachine)
            {
                var controller = GetController(vendingMachine);
                if (controller == null)
                    return;

                controller.RemoveVendingMachine(vendingMachine);
                _controllersByVendingMachine.Remove(vendingMachine.net.ID);

                if (!controller.HasVendingMachines)
                {
                    _uniqueControllers.Remove(controller);

                    var customController = controller as CustomVendingController;
                    if (customController != null)
                    {
                        _controllersByDataProvider.Remove(customController.DataProvider);
                        _dataProviderRegistry.Unregister(customController.DataProvider);
                    }
                }
            }

            public BaseVendingController GetController(NPCVendingMachine vendingMachine)
            {
                BaseVendingController controller;
                return _controllersByVendingMachine.TryGetValue(vendingMachine.net.ID, out controller)
                    ? controller
                    : null;
            }

            public void SetupAll()
            {
                foreach (var entity in BaseNetworkable.serverEntities)
                {
                    var vendingMachine = entity as NPCVendingMachine;
                    if (vendingMachine == null)
                        continue;

                    HandleVendingMachineSpawned(vendingMachine);
                }
            }

            public void ResetAll()
            {
                foreach (var controller in _controllersByVendingMachine.Values)
                {
                    controller.Destroy();
                }
            }

            private MonumentVendingController GetControllerByLocation(MonumentRelativePosition location)
            {
                foreach (var controller in _uniqueControllers)
                {
                    var locationBasedController = controller as MonumentVendingController;
                    if (locationBasedController == null)
                    {
                        continue;
                    }

                    if (LocationsMatch(locationBasedController.Location, location))
                        return locationBasedController;
                }

                return null;
            }

            private MonumentVendingController EnsureMonumentController(MonumentRelativePosition location)
            {
                var controller = GetControllerByLocation(location);
                if (controller != null)
                {
                    return controller;
                }

                controller = new MonumentVendingController(_plugin, _componentFactory, location);
                _uniqueControllers.Add(controller);

                return controller;
            }

            private CustomVendingController GetControllerByDataProvider(DataProvider dataProvider)
            {
                CustomVendingController controller;
                return _controllersByDataProvider.TryGetValue(dataProvider, out controller)
                    ? controller
                    : null;
            }

            private CustomVendingController EnsureCustomController(DataProvider dataProvider)
            {
                var controller = GetControllerByDataProvider(dataProvider);
                if (controller != null)
                {
                    return controller;
                }

                controller = new CustomVendingController(_plugin, _componentFactory, dataProvider);
                _controllersByDataProvider[dataProvider] = controller;
                _uniqueControllers.Add(controller);

                return controller;
            }
        }

        #endregion

        #region Edit Controller

        private class EditContainerComponent : FacepunchBehaviour
        {
            public static void AddToContainer(CustomVendingSetup plugin, StorageContainer container, EditController editController)
            {
                var component = container.GetOrAddComponent<EditContainerComponent>();
                component._plugin = plugin;
                component._editController = editController;
            }

            private CustomVendingSetup _plugin;
            private EditController _editController;

            private void PlayerStoppedLooting(BasePlayer player)
            {
                _plugin.TrackStart();
                _editController.HandlePlayerLootEnd(player);
                _plugin.TrackEnd();
            }
        }

        private class EditController
        {
            private static void OpenEditPanel(BasePlayer player, StorageContainer containerEntity)
            {
                var playerLoot = player.inventory.loot;
                playerLoot.Clear();
                playerLoot.PositionChecks = false;
                playerLoot.entitySource = containerEntity;
                playerLoot.itemSource = null;
                playerLoot.MarkDirty();
                playerLoot.AddContainer(containerEntity.inventory);
                playerLoot.SendImmediate();
                player.ClientRPCPlayer(null, player, "RPC_OpenLootPanel", containerEntity.panelName);
            }

            public BasePlayer EditorPlayer { get; private set; }

            private CustomVendingSetup _plugin;
            private BaseVendingController _vendingController;
            private NPCVendingMachine _vendingMachine;
            private StorageContainer _container;
            private EditFormState _formState;

            public EditController(CustomVendingSetup plugin, BaseVendingController vendingController, NPCVendingMachine vendingMachine, BasePlayer editorPlayer)
            {
                _plugin = plugin;
                _vendingController = vendingController;
                _vendingMachine = vendingMachine;
                EditorPlayer = editorPlayer;

                var offers = vendingController.Profile?.Offers ?? GetOffersFromVendingMachine(vendingMachine);

                _container = CreateOrdersContainer(plugin, editorPlayer, offers, vendingMachine.shopName);
                _formState = EditFormState.FromVendingMachine(vendingController, vendingMachine);
                EditContainerComponent.AddToContainer(plugin, _container, this);
                _container.SendAsSnapshot(editorPlayer.Connection);
                OpenEditPanel(editorPlayer, _container);

                CuiHelper.AddUi(editorPlayer, ContainerUIRenderer.RenderContainerUI(plugin, editorPlayer, vendingMachine, _formState));
            }

            public void ToggleBroadcast()
            {
                _formState.Broadcast = !_formState.Broadcast;

                CuiHelper.DestroyUi(EditorPlayer, ContainerUIRenderer.BroadcastUIName);
                CuiHelper.AddUi(EditorPlayer, ContainerUIRenderer.RenderBroadcastUI(EditorPlayer, _vendingMachine, _formState));
            }

            public void ToggleDroneAccessible()
            {
                if (!_formState.Broadcast)
                {
                    _formState.DroneAccessible = true;
                    _formState.Broadcast = true;
                }
                else
                {
                    _formState.DroneAccessible = !_formState.DroneAccessible;
                }

                CuiHelper.DestroyUi(EditorPlayer, ContainerUIRenderer.BroadcastUIName);
                CuiHelper.AddUi(EditorPlayer, ContainerUIRenderer.RenderBroadcastUI(EditorPlayer, _vendingMachine, _formState));
            }

            public void ApplyStateTo(VendingProfile profile)
            {
                profile.Offers = GetOffersFromContainer(_plugin, EditorPlayer, _container.inventory);
                profile.Broadcast = _formState.Broadcast;
                profile.DroneAccessible = _formState.DroneAccessible;

                var updatedShopName = _container.inventory.GetSlot(ShopNameNoteSlot)?.text.Trim();
                if (!string.IsNullOrEmpty(updatedShopName))
                {
                    profile.ShopName = updatedShopName;
                }
            }

            public void HandlePlayerLootEnd(BasePlayer player)
            {
                Kill();
            }

            public void Kill()
            {
                DestroyUI();
                KillContainer();
                _vendingController.OnEditControllerKilled();
            }

            private void DestroyUI()
            {
                CuiHelper.DestroyUi(EditorPlayer, ContainerUIRenderer.UIName);
            }

            private void KillContainer()
            {
                if (_container == null || _container.IsDestroyed)
                {
                    return;
                }

                if (EditorPlayer != null && !EditorPlayer.IsDestroyed && EditorPlayer.IsConnected)
                {
                    _container.OnNetworkSubscribersLeave(new List<Network.Connection> { EditorPlayer.Connection });
                }

                _plugin._bagOfHoldingLimitManager.RemoveLimitProfile(_container.inventory);
                _container.Kill();
                _container = null;
            }
        }

        #endregion

        #region Vending Machine Controller

        private abstract class BaseVendingController
        {
            // While the Profile is null, the vending machines will be vanilla.
            public VendingProfile Profile { get; protected set; }

            // While the EditController is non-null, a player is editing the vending machine.
            public EditController EditController { get; protected set; }

            public bool HasVendingMachines => _vendingMachineList.Count > 0;

            protected CustomVendingSetup _plugin;

            // List of vending machines with a position matching this controller.
            private HashSet<NPCVendingMachine> _vendingMachineList = new HashSet<NPCVendingMachine>();

            private ComponentFactory<NPCVendingMachine, VendingMachineComponent> _componentFactory;

            private string _cachedShopUI;

            protected BaseVendingController(CustomVendingSetup plugin, ComponentFactory<NPCVendingMachine, VendingMachineComponent> componentFactory)
            {
                _plugin = plugin;
                _componentFactory = componentFactory;
            }

            protected abstract void SaveProfile(VendingProfile vendingProfile);

            protected abstract void DeleteProfile(VendingProfile vendignProfile);

            public void StartEditing(BasePlayer player, NPCVendingMachine vendingMachine)
            {
                if (EditController != null)
                    return;

                EditController = new EditController(_plugin, this, vendingMachine, player);
            }

            public void HandleReset()
            {
                DeleteProfile(Profile);
                Profile = null;
                SetupVendingMachines();
                EditController?.Kill();
                _plugin._inaccessibleVendingMachines.Remove(this);

                _cachedShopUI = null;
            }

            public void Destroy()
            {
                ResetVendingMachines();
                EditController?.Kill();
            }

            public void HandleSave(NPCVendingMachine vendingMachine)
            {
                CreateOrUpdateProfile(vendingMachine);

                EditController.ApplyStateTo(Profile);
                EditController.Kill();

                SaveProfile(Profile);
                SetupVendingMachines();

                _cachedShopUI = null;

                UpdateDroneAccessibility();
            }

            public void AddVendingMachine(NPCVendingMachine vendingMachine)
            {
                if (!_vendingMachineList.Add(vendingMachine))
                    return;

                var component = _componentFactory.GetOrAddTo(vendingMachine);
                component.SetController(this);
                component.SetProfile(Profile);
            }

            public void RemoveVendingMachine(NPCVendingMachine vendingMachine)
            {
                if (!_vendingMachineList.Remove(vendingMachine))
                    return;

                if (_vendingMachineList.Count == 0)
                {
                    EditController?.Kill();
                    _plugin._inaccessibleVendingMachines.Remove(this);
                }
            }

            public void OnEditControllerKilled()
            {
                EditController = null;
            }

            public string GetShopUI()
            {
                if (_cachedShopUI == null)
                {
                    _cachedShopUI = ShopUIRenderer.RenderShopUI(Profile);
                }

                return _cachedShopUI;
            }

            public void UpdateDroneAccessibility()
            {
                if (Profile == null)
                    return;

                if (Profile.Broadcast && !Profile.DroneAccessible)
                {
                    _plugin._inaccessibleVendingMachines.Add(this);
                }
                else
                {
                    _plugin._inaccessibleVendingMachines.Remove(this);
                }
            }

            protected virtual void CreateOrUpdateProfile(NPCVendingMachine vendingMachine)
            {
                if (Profile == null)
                {
                    Profile = VendingProfile.FromVendingMachine(vendingMachine);
                }
            }

            private void SetupVendingMachines()
            {
                foreach (var vendingMachine in _vendingMachineList)
                {
                    _componentFactory.GetOrAddTo(vendingMachine).SetProfile(Profile);
                }
            }

            private void ResetVendingMachines()
            {
                foreach (var vendingMachine in _vendingMachineList)
                {
                    VendingMachineComponent.RemoveFromVendingMachine(vendingMachine);
                }
            }
        }

        private class CustomVendingController : BaseVendingController
        {
            public DataProvider DataProvider { get; private set; }

            public CustomVendingController(CustomVendingSetup plugin, ComponentFactory<NPCVendingMachine, VendingMachineComponent> componentFactory, DataProvider dataProvider)
                : base(plugin, componentFactory)
            {
                DataProvider = dataProvider;
                Profile = dataProvider.GetData();
                UpdateDroneAccessibility();
            }

            protected override void SaveProfile(VendingProfile vendingProfile)
            {
                Profile = vendingProfile;
                DataProvider.SaveData(vendingProfile);
            }

            protected override void DeleteProfile(VendingProfile vendignProfile)
            {
                DataProvider.SaveData(null);
            }
        }

        private class MonumentVendingController : BaseVendingController
        {
            public MonumentRelativePosition Location { get; private set; }

            private SavedData _pluginData => _plugin._pluginData;

            public MonumentVendingController(CustomVendingSetup plugin, ComponentFactory<NPCVendingMachine, VendingMachineComponent> componentFactory, MonumentRelativePosition location)
                : base(plugin, componentFactory)
            {
                Location = location;
                Profile = _pluginData.FindProfile(location);
                UpdateDroneAccessibility();
            }

            protected override void SaveProfile(VendingProfile vendingProfile)
            {
                if (!_pluginData.VendingProfiles.Contains(vendingProfile))
                {
                    _pluginData.VendingProfiles.Add(vendingProfile);
                }

                _pluginData.Save();
            }

            protected override void DeleteProfile(VendingProfile vendignProfile)
            {
                _pluginData.VendingProfiles.Remove(vendignProfile);
                _pluginData.Save();
            }

            protected override void CreateOrUpdateProfile(NPCVendingMachine vendingMachine)
            {
                // Update the location, in case the vending machine has moved.
                Location = MonumentRelativePosition.FromVendingMachine(_plugin._monumentFinderAdapter, vendingMachine);

                if (Profile == null)
                {
                    Profile = VendingProfile.FromVendingMachine(vendingMachine, Location);
                }
                else
                {
                    Profile.Position = Location.GetPosition();
                }
            }
        }

        #endregion

        #region Component Tracker & Factory

        private class ComponentTracker<THost, TGuest>
            where THost : UnityEngine.Component
            where TGuest : UnityEngine.Component
        {
            private readonly Dictionary<THost, TGuest> _hostToGuest = new Dictionary<THost, TGuest>();

            public void RegisterComponent(THost host, TGuest guest)
            {
                _hostToGuest[host] = guest;
            }

            public TGuest GetComponent(THost host)
            {
                TGuest guest;
                return _hostToGuest.TryGetValue(host, out guest)
                    ? guest
                    : null;
            }

            public void UnregisterComponent(THost source)
            {
                _hostToGuest.Remove(source);
            }
        }

        private class TrackedComponent<THost, TGuest> : FacepunchBehaviour
            where THost : UnityEngine.Component
            where TGuest : TrackedComponent<THost, TGuest>
        {
            public CustomVendingSetup Plugin;
            public ComponentTracker<THost, TGuest> ComponentTracker;
            public THost Host;

            public virtual void OnCreated() {}

            protected virtual void OnDestroy()
            {
                ComponentTracker?.UnregisterComponent(Host);
            }
        }

        private class ComponentFactory<THost, TGuest>
            where THost : UnityEngine.Component
            where TGuest : TrackedComponent<THost, TGuest>
        {
            private CustomVendingSetup _plugin;
            private ComponentTracker<THost, TGuest> _componentTracker;

            public ComponentFactory(CustomVendingSetup plugin, ComponentTracker<THost, TGuest> componentTracker)
            {
                _plugin = plugin;
                _componentTracker = componentTracker;
            }

            public TGuest GetOrAddTo(THost host)
            {
                var guest = _componentTracker.GetComponent(host);
                if (guest == null)
                {
                    guest = host.gameObject.AddComponent<TGuest>();
                    guest.Plugin = _plugin;
                    guest.ComponentTracker = _componentTracker;
                    guest.Host = host;
                    guest.OnCreated();
                    _componentTracker.RegisterComponent(host, guest);
                }

                return guest;
            }
        }

        #endregion

        #region Vending Machine Component

        private class VendingMachineComponent : TrackedComponent<NPCVendingMachine, VendingMachineComponent>
        {
            public static void RemoveFromVendingMachine(NPCVendingMachine vendingMachine) =>
                DestroyImmediate(vendingMachine.GetComponent<VendingMachineComponent>());

            public VendingProfile Profile { get; private set; }

            private readonly List<BasePlayer> _adminUIViewers = new List<BasePlayer>();
            private readonly List<BasePlayer> _shopUIViewers = new List<BasePlayer>();
            private BaseVendingController _vendingController;
            private NPCVendingMachine _vendingMachine;
            private float[] _refillTimes;

            private string _originalShopName;
            private bool? _originalBroadcast;

            public override void OnCreated()
            {
                _vendingMachine = Host;
            }

            public void ShowAdminUI(BasePlayer player)
            {
                _adminUIViewers.Add(player);
                CuiHelper.DestroyUi(player, AdminUIRenderer.UIName);
                CuiHelper.AddUi(player, AdminUIRenderer.RenderAdminUI(Plugin, player, _vendingMachine, Profile));
            }

            public void ShowShopUI(BasePlayer player)
            {
                var json = _vendingController.GetShopUI();
                if (json == string.Empty)
                    return;

                _shopUIViewers.Add(player);
                CuiHelper.DestroyUi(player, ShopUIRenderer.UIName);
                CuiHelper.AddUi(player, json);
            }

            public void RemoveUI(BasePlayer player)
            {
                if (_adminUIViewers.Remove(player))
                {
                    DestroyAdminUI(player);
                }

                if (_shopUIViewers.Remove(player))
                {
                    DestroyShopUI(player);
                }
            }

            protected override void OnDestroy()
            {
                base.OnDestroy();

                DestroyUIs();

                if (Profile?.Offers != null && (_vendingMachine != null && !_vendingMachine.IsDestroyed))
                {
                    ResetToVanilla();
                }
            }

            private void PlayerStoppedLooting(BasePlayer player)
            {
                Plugin.TrackStart();
                RemoveUI(player);
                Plugin.TrackEnd();
            }

            public void SetController(BaseVendingController vendingController)
            {
                _vendingController = vendingController;
            }

            public void SetProfile(VendingProfile profile)
            {
                if (Profile == null && profile != null)
                {
                    DisableVanillaBehavior();
                }
                else if (Profile != null && profile == null)
                {
                    ResetToVanilla();
                }

                Profile = profile;

                if (profile?.Offers == null)
                    return;

                _refillTimes = new float[Profile.Offers.Length];

                for (var i = _vendingMachine.inventory.itemList.Count - 1; i >= 0; i--)
                {
                    var item = _vendingMachine.inventory.itemList[i];
                    item.RemoveFromContainer();
                    item.Remove();
                }

                _vendingMachine.ClearSellOrders();

                if (_originalShopName == null)
                    _originalShopName = _vendingMachine.shopName;

                if (_originalBroadcast == null)
                    _originalBroadcast = _vendingMachine.IsBroadcasting();

                if (!string.IsNullOrEmpty(profile.ShopName))
                {
                    _vendingMachine.shopName = profile.ShopName;
                }

                if (_vendingMachine.IsBroadcasting() != profile.Broadcast)
                {
                    _vendingMachine.SetFlag(VendingMachineFlags.Broadcasting, profile.Broadcast);
                    _vendingMachine.UpdateMapMarker();
                }

                for (var i = 0; i < profile.Offers.Length && i < MaxVendingOffers; i++)
                {
                    var offer = profile.Offers[i];
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

                    Interface.CallHook("OnAddVendingOffer", _vendingMachine, vendingOffer);
                    _vendingMachine.sellOrders.sellOrders.Add(vendingOffer);
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
                if (_vendingMachine.IsDestroyed)
                {
                    return;
                }

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

                    var itemQuery = ItemQuery.FromSellItem(offer.SellItem);
                    var numPurchasesInStock = ItemUtils.SumContainerItems(_vendingMachine.inventory, ref itemQuery) / offer.SellItem.Amount;
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

                    int refillAmount;

                    try
                    {
                        refillAmount = checked(refillNumberOfPurchases * offer.SellItem.Amount);
                    }
                    catch (OverflowException ex)
                    {
                        LogError($"Cannot multiply {refillNumberOfPurchases} by {offer.SellItem.Amount} because the result is too large. You have misconfigured the plugin. It is not necessary to stock that much of any item. Please reduce Max Stock or Refill Amount for item {offer.SellItem.ShortName}.\n" + ex.ToString());

                        // Prevent further refills to avoid spamming the console since this case cannot be fixed without editing the vending machine.
                        StopRefilling(offerIndex);
                        continue;
                    }

                    // Always increase the quantity of an existing item if present, rather than creating a new item.
                    // This is done to prevent ridiculous configurations from potentially filling up the vending machine with specific items.
                    var existingItem = ItemUtils.FindFirstContainerItem(_vendingMachine.inventory, ref itemQuery);
                    if (existingItem != null)
                    {
                        try
                        {
                            existingItem.amount = checked(existingItem.amount + refillAmount);
                            existingItem.MarkDirty();
                            ScheduleRefill(offerIndex, offer);
                        }
                        catch (OverflowException ex)
                        {
                            LogError($"Cannot add {refillAmount} to {existingItem.amount} because the result is too large. You have misconfigured the plugin. It is not necessary to stock that much of any item. Please reduce Max Stock or Refill Amount for item {offer.SellItem.ShortName}.\n" + ex.ToString());

                            // Reduce refill rate to avoid spamming the console.
                            ScheduleDelayedRefill(offerIndex, offer);
                        }
                        continue;
                    }

                    var item = offer.SellItem.Create(refillAmount);
                    if (item == null)
                    {
                        LogError($"Unable to create item '{offer.SellItem.ShortName}'. Does that item exist? Was it removed from the game?");

                        // Prevent further refills to avoid spamming the console since this case cannot be fixed without editing the vending machine.
                        StopRefilling(offerIndex);
                        continue;
                    }

                    _vendingMachine.transactionActive = true;

                    if (item.MoveToContainer(_vendingMachine.inventory, allowStack: false))
                    {
                        ScheduleRefill(offerIndex, offer);
                    }
                    else
                    {
                        LogError($"Unable to add {item.amount} '{item.info.shortname}' because the vending machine container rejected it.");

                        item.Remove();

                        // Reduce refill rate to avoid spamming the console.
                        ScheduleDelayedRefill(offerIndex, offer);
                    }

                    _vendingMachine.transactionActive = false;
                }
            }

            private void TimedRefill() => CustomRefill();

            private void DestroyAdminUI(BasePlayer player)
            {
                CuiHelper.DestroyUi(player, AdminUIRenderer.UIName);
            }

            private void DestroyShopUI(BasePlayer player)
            {
                CuiHelper.DestroyUi(player, ShopUIRenderer.UIName);
            }

            private void DestroyUIs()
            {
                foreach (var player in _adminUIViewers)
                {
                    DestroyAdminUI(player);
                }

                foreach (var player in _shopUIViewers)
                {
                    DestroyShopUI(player);
                }
            }

            private void DisableVanillaBehavior()
            {
                _vendingMachine.CancelInvoke(_vendingMachine.InstallFromVendingOrders);
                _vendingMachine.CancelInvoke(_vendingMachine.Refill);

                InvokeRandomized(TimedRefill, 1, 1, 0.1f);
            }

            private void ResetToVanilla()
            {
                CancelInvoke(TimedRefill);

                if (_originalShopName != null)
                {
                    _vendingMachine.shopName = _originalShopName;
                }

                if (_originalBroadcast != null && _originalBroadcast != _vendingMachine.IsBroadcasting())
                {
                    _vendingMachine.SetFlag(VendingMachineFlags.Broadcasting, _originalBroadcast.Value);
                    _vendingMachine.UpdateMapMarker();
                }

                _vendingMachine.InstallFromVendingOrders();
                _vendingMachine.InvokeRandomized(_vendingMachine.Refill, 1f, 1f, 0.1f);
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

        private class CaseInsensitiveDictionary<TValue> : Dictionary<string, TValue>
        {
            public CaseInsensitiveDictionary() : base(StringComparer.OrdinalIgnoreCase) {}
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class VendingItem
        {
            public static VendingItem FromItem(Item item)
            {
                ItemDefinition ammoType;
                var ammoAmount = GetAmmoAmountAndType(item, out ammoType);

                return new VendingItem
                {
                    ShortName = item.IsBlueprint() ? item.blueprintTargetDef.shortname : item.info.shortname,
                    Amount = item.amount,
                    DisplayName = item.name,
                    SkinId = item.skin,
                    IsBlueprint = item.blueprintTarget != 0,
                    DataInt = item.instanceData?.dataInt ?? 0,
                    AmmoAmount = ammoAmount,
                    AmmoType = ammoType?.shortname,
                    Position = item.position,
                    Capacity = item.contents?.capacity ?? 0,
                    Contents = item.contents?.itemList?.Count > 0 ? SerializeContents(item.contents.itemList) : null,
                };
            }

            private static List<VendingItem> SerializeContents(List<Item> itemList)
            {
                var vendingItemList = new List<VendingItem>(itemList.Count);

                foreach (var item in itemList)
                {
                    vendingItemList.Add(FromItem(item));
                }

                return vendingItemList;
            }

            private static int GetAmmoAmountAndType(Item item, out ItemDefinition ammoType)
            {
                ammoType = null;

                var heldEntity = item.GetHeldEntity();
                if (heldEntity == null)
                    return -1;

                var baseProjectile = heldEntity as BaseProjectile;
                if ((object)baseProjectile != null)
                {
                    ammoType = baseProjectile.primaryMagazine?.ammoType;
                    return baseProjectile.primaryMagazine?.contents ?? 0;
                }

                var flameThrower = heldEntity as FlameThrower;
                if ((object)flameThrower != null)
                {
                    return flameThrower.ammo;
                }

                return -1;
            }

            [JsonProperty("ShortName")]
            public string ShortName;

            [JsonProperty("DisplayName", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string DisplayName;

            [JsonProperty("Amount")]
            public int Amount = 1;

            [JsonProperty("Skin", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public ulong SkinId;

            [JsonProperty("IsBlueprint", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool IsBlueprint;

            [JsonProperty("DataInt", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int DataInt;

            [JsonProperty("Position", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int Position;

            [JsonProperty("Ammo", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(-1)]
            public int AmmoAmount = -1;

            [JsonProperty("AmmoType", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string AmmoType;

            [JsonProperty("Capacity", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int Capacity;

            [JsonProperty("Contents", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public List<VendingItem> Contents;

            private ItemDefinition _itemDefinition;
            public ItemDefinition Definition
            {
                get
                {
                    if ((object)_itemDefinition == null && ShortName != null)
                    {
                        _itemDefinition = ItemManager.FindItemDefinition(ShortName);
                    }

                    return _itemDefinition;
                }
            }

            private ItemDefinition _ammoTypeDefinition;
            public ItemDefinition AmmoTypeDefinition
            {
                get
                {
                    if ((object)_ammoTypeDefinition == null && AmmoType != null)
                    {
                        _ammoTypeDefinition = ItemManager.FindItemDefinition(AmmoType);
                    }

                    return _ammoTypeDefinition;
                }
            }

            public bool IsValid => (object)Definition != null;
            public int ItemId => Definition.itemid;

            public Item Create(int amount)
            {
                Item item;
                if (IsBlueprint)
                {
                    item = ItemManager.CreateByItemID(BlueprintItemId, amount, SkinId);
                    item.blueprintTarget = Definition.itemid;
                }
                else
                {
                    item = ItemManager.Create(Definition, amount, SkinId);
                }

                if (item == null)
                    return null;

                item.name = DisplayName;
                item.position = Position;

                if (DataInt != 0)
                {
                    if (item.instanceData == null)
                    {
                        item.instanceData = new ProtoBuf.Item.InstanceData();
                        item.instanceData.ShouldPool = false;
                    }

                    item.instanceData.dataInt = DataInt;
                }

                if (Contents != null && Contents.Count > 0)
                {
                    if (item.contents == null)
                    {
                        item.contents = new ItemContainer();
                        item.contents.ServerInitialize(null, Math.Max(Capacity, Contents.Count));
                        item.contents.GiveUID();
                        item.contents.parent = item;
                    }
                    else
                    {
                        item.contents.capacity = Math.Max(item.contents.capacity, Capacity);
                    }

                    foreach (var childItemSpec in Contents)
                    {
                        var childItem = childItemSpec.Create(childItemSpec.Amount);
                        if (!childItem.MoveToContainer(item.contents, childItemSpec.Position))
                        {
                            childItem.Remove();
                        }
                    }
                }

                var heldEntity = item.GetHeldEntity();
                if (heldEntity != null)
                {
                    var baseProjectile = heldEntity as BaseProjectile;
                    if ((object)baseProjectile != null)
                    {
                        var magazine = baseProjectile.primaryMagazine;
                        if (magazine != null)
                        {
                            if (AmmoAmount >= 0)
                            {
                                magazine.contents = AmmoAmount;
                            }

                            if (AmmoType != null)
                            {
                                magazine.ammoType = AmmoTypeDefinition;
                            }
                        }
                    }

                    var flameThrower = heldEntity as FlameThrower;
                    if ((object)flameThrower != null)
                    {
                        flameThrower.ammo = AmmoAmount;
                    }
                }

                // Set the placeholder flag so that Enchanted Items doesn't transform the artifact into an enchanted item yet.
                item.SetFlag(Item.Flag.Placeholder, true);

                return item;
            }

            public Item Create() => Create(Amount);

            public Item Split(Item item, int amount)
            {
                if (amount <= 0 || amount >= item.amount)
                    return null;

                item.amount -= amount;
                item.MarkDirty();

                return Create(amount);
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
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

            public static VendingOffer FromItems(CustomVendingSetup plugin, BasePlayer player, Item sellItem, Item currencyItem, Item settingsItem)
            {
                var offer = new VendingOffer
                {
                    SellItem = VendingItem.FromItem(sellItem),
                    CurrencyItem = VendingItem.FromItem(currencyItem),
                };

                offer.SellItem.Position = 0;
                offer.CurrencyItem.Position = 0;

                if (settingsItem != null)
                {
                    var refillMaxLabel = plugin.GetMessage(player, Lang.SettingsRefillMax);
                    var refillDelayLabel = plugin.GetMessage(player, Lang.SettingsRefillDelay);
                    var refillAmountLabel = plugin.GetMessage(player, Lang.SettingsRefillAmount);

                    var localizedSettings = ParseSettingsItem(settingsItem);

                    int refillMax;
                    if (TryParseIntKey(localizedSettings, refillMaxLabel, out refillMax))
                        offer.RefillMax = refillMax;

                    int refillDelay;
                    if (TryParseIntKey(localizedSettings, refillDelayLabel, out refillDelay))
                        offer.RefillDelay = refillDelay;

                    int refillAmount;
                    if (TryParseIntKey(localizedSettings, refillAmountLabel, out refillAmount))
                        offer.RefillAmount = refillAmount;

                    // Allow other plugins to parse the settings and populate custom settings.
                    // Other plugins determine data file keys, as well as localized option names.
                    var customSettings = new CaseInsensitiveDictionary<object>();
                    ExposedHooks.OnCustomVendingSetupOfferSettingsParse(localizedSettings, customSettings);
                    if (customSettings.Count > 0)
                    {
                        offer.CustomSettings = customSettings;
                    }
                }

                return offer;
            }

            private static CaseInsensitiveDictionary<string> ParseSettingsItem(Item settingsItem)
            {
                var dict = new CaseInsensitiveDictionary<string>();
                if (string.IsNullOrEmpty(settingsItem.text))
                    return dict;

                foreach (var line in settingsItem.text.Split('\n'))
                {
                    var parts = line.Split(':');
                    if (parts.Length < 2)
                        continue;

                    dict[parts[0].Trim()] = parts[1].Trim();
                }

                return dict;
            }

            private static bool TryParseIntKey(Dictionary<string, string> dict, string key, out int result)
            {
                result = 0;
                string stringValue;
                return dict.TryGetValue(key, out stringValue)
                    && int.TryParse(stringValue, out result);
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

            [JsonProperty("CustomSettings", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public CaseInsensitiveDictionary<object> CustomSettings;

            public bool IsValid => SellItem.IsValid && CurrencyItem.IsValid;
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class VendingProfile : IMonumentRelativePosition
        {
            public static VendingProfile FromVendingMachine(NPCVendingMachine vendingMachine, MonumentRelativePosition location = null)
            {
                return new VendingProfile
                {
                    ShopName = vendingMachine.shopName,
                    Broadcast = vendingMachine.IsBroadcasting(),
                    Monument = location?.GetMonumentPrefabName(),
                    MonumentAlias = location?.GetMonumentAlias(),
                    Position = location?.GetPosition() ?? Vector3.zero,
                };
            }

            [JsonProperty("ShopName", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string ShopName;

            [JsonProperty("Broadcast", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(true)]
            public bool Broadcast = true;

            [JsonProperty("DroneAccessible", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(true)]
            public bool DroneAccessible = true;

            [JsonProperty("Monument", DefaultValueHandling = DefaultValueHandling.Ignore)]
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

        [JsonObject(MemberSerialization.OptIn)]
        private class SavedData
        {
            // Legacy data for v1.
            [JsonProperty("Vendings", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public List<LegacyVendingProfile> Vendings;

            [JsonProperty("VendingProfiles")]
            public List<VendingProfile> VendingProfiles = new List<VendingProfile>();

            public static SavedData Load()
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<SavedData>(nameof(CustomVendingSetup)) ?? new SavedData();

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
                                    SkinId = legacyOffer.SellItem.Skin,
                                    IsBlueprint = legacyOffer.SellItem.IsBlueprint,
                                },
                                CurrencyItem = new VendingItem
                                {
                                    ShortName = legacyOffer.Currency.Shortname,
                                    DisplayName = !string.IsNullOrEmpty(legacyOffer.Currency.DisplayName) ? legacyOffer.Currency.DisplayName : null,
                                    Amount = legacyOffer.Currency.Amount,
                                    SkinId = legacyOffer.Currency.Skin,
                                    IsBlueprint = legacyOffer.Currency.IsBlueprint,
                                },
                            };
                        }

                        data.VendingProfiles.Add(profile);
                    }

                    dataMigrated = data.Vendings.Count > 0;
                    data.Vendings = null;
                    data.Save();
                    LogWarning($"Migrated data file to new format.");
                }

                return data;
            }

            public void Save() =>
                Interface.Oxide.DataFileSystem.WriteObject(nameof(CustomVendingSetup), this);

            public VendingProfile FindProfile(IMonumentRelativePosition location)
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

        #region Configuration

        private class ShopUISettings
        {
            [JsonProperty("Enable skin overlays")]
            public bool EnableSkinOverlays = false;
        }

        private class Configuration : SerializableConfiguration
        {
            [JsonProperty("Shop UI settings")]
            public ShopUISettings ShopUISettings = new ShopUISettings();

            [JsonProperty("Override item max stack sizes (shortname: amount)")]
            public Dictionary<string, int> ItemStackSizeOverrides = new Dictionary<string, int>();

            public void Init()
            {
                foreach (var entry in ItemStackSizeOverrides)
                {
                    if (ItemManager.FindItemDefinition(entry.Key) == null)
                    {
                        LogError($"Invalid item in config: {entry.Key}");
                        continue;
                    }
                }
            }

            public int GetItemMaxStackSize(Item item)
            {
                var maxStackSize = item.MaxStackable();

                int overrideMaxStackSize;
                if (ItemStackSizeOverrides.TryGetValue(item.info.shortname, out overrideMaxStackSize))
                {
                    maxStackSize = Math.Max(maxStackSize, overrideMaxStackSize);
                }

                return Math.Max(1, maxStackSize);
            }
        }

        private Configuration GetDefaultConfig() => new Configuration();

        #region Configuration Helpers

        private class SerializableConfiguration
        {
            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonHelper.Deserialize(ToJson()) as Dictionary<string, object>;
        }

        private static class JsonHelper
        {
            public static object Deserialize(string json) => ToObject(JToken.Parse(json));

            private static object ToObject(JToken token)
            {
                switch (token.Type)
                {
                    case JTokenType.Object:
                        return token.Children<JProperty>()
                                    .ToDictionary(prop => prop.Name,
                                                  prop => ToObject(prop.Value));

                    case JTokenType.Array:
                        return token.Select(ToObject).ToList();

                    default:
                        return ((JValue)token).Value;
                }
            }
        }

        private bool MaybeUpdateConfig(SerializableConfiguration config)
        {
            var currentWithDefaults = config.ToDictionary();
            var currentRaw = Config.ToDictionary(x => x.Key, x => x.Value);
            return MaybeUpdateConfigDict(currentWithDefaults, currentRaw);
        }

        private bool MaybeUpdateConfigDict(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
        {
            bool changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                object currentRawValue;
                if (currentRaw.TryGetValue(key, out currentRawValue))
                {
                    var defaultDictValue = currentWithDefaults[key] as Dictionary<string, object>;
                    var currentDictValue = currentRawValue as Dictionary<string, object>;

                    if (defaultDictValue != null)
                    {
                        if (currentDictValue == null)
                        {
                            currentRaw[key] = currentWithDefaults[key];
                            changed = true;
                        }
                        else if (MaybeUpdateConfigDict(defaultDictValue, currentDictValue))
                            changed = true;
                    }
                }
                else
                {
                    currentRaw[key] = currentWithDefaults[key];
                    changed = true;
                }
            }

            return changed;
        }

        protected override void LoadDefaultConfig() => _pluginConfig = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _pluginConfig = Config.ReadObject<Configuration>();
                if (_pluginConfig == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(_pluginConfig))
                {
                    LogWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch (Exception e)
            {
                LogError(e.Message);
                LogWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Log($"Configuration changes saved to {Name}.json");
            Config.WriteObject(_pluginConfig, true);
        }

        #endregion

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

        private static class Lang
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
