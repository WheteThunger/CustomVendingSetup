﻿// #define DEBUG_READONLY

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using ProtoBuf;
using UnityEngine;
using VLB;
using static ProtoBuf.VendingMachine;
using static VendingMachine;

using CustomGetDataCallback = System.Func<Newtonsoft.Json.Linq.JObject>;
using CustomSaveDataCallback = System.Action<Newtonsoft.Json.Linq.JObject>;
using CustomGetSkinCallback = System.Func<ulong>;
using CustomSetSkinCallback = System.Action<ulong>;
using Pool = Facepunch.Pool;
using Time = UnityEngine.Time;

namespace Oxide.Plugins
{
    [Info("Custom Vending Setup", "WhiteThunder", "2.14.3")]
    [Description("Allows editing orders at NPC vending machines.")]
    internal class CustomVendingSetup : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private readonly Plugin BagOfHolding, Economics, ItemRetriever, MonumentFinder, ServerRewards;

        private SavedPrefabRelativeData _prefabRelativeData;
        private SavedMapData _mapData;
        private SavedSalesData _salesData;
        private Configuration _config;

        private const string PermissionUse = "customvendingsetup.use";

        private const string StoragePrefab = "assets/prefabs/deployable/large wood storage/box.wooden.large.prefab";

        private const int ItemsPerRow = 6;
        private const int InventorySize = 24;

        // Going over 7 causes offers to get cut off regardless of resolution.
        private const int MaxVendingOffers = 7;

        private const int GeneralSettingsNoteSlot = 29;
        private const int ContainerCapacity = 30;
        private const int MaxItemRows = ContainerCapacity / ItemsPerRow;
        private const int BlueprintItemId = -996920608;
        private const float MinCurrencyCondition = 0.5f;

        private const ulong NpcVendingMachineSkinId = 861142659;

        private static readonly Regex KeyValueRegex = new(@"^([^:]+):(.+(?:\n[^:\n]+$)*)", RegexOptions.Compiled | RegexOptions.Multiline);

        private readonly object True = true;
        private readonly object False = false;

        private ItemRetrieverAdapter _itemRetrieverAdapter;
        private PluginDataProviderRegistry _dataProviderRegistry = new();
        private ComponentTracker<NPCVendingMachine, VendingMachineComponent> _componentTracker = new();
        private ComponentFactory<NPCVendingMachine, VendingMachineComponent> _componentFactory;
        private MonumentFinderAdapter _monumentFinderAdapter;
        private VendingMachineManager _vendingMachineManager;
        private BagOfHoldingLimitManager _bagOfHoldingLimitManager;
        private DynamicHookSubscriber<VendingController> _inaccessibleVendingMachines;
        private DynamicHookSubscriber<BasePlayer> _playersNeedingFakeInventory;
        private PaymentProviderResolver _paymentProviderResolver;

        private ItemDefinition _noteItemDefinition;
        private bool _isServerInitialized;
        private bool _performingInstantRestock;
        private VendingItem _itemBeingSold;
        private Dictionary<string, object> _itemRetrieverQuery = new();
        private List<Item> _reusableItemList = new();
        private object[] _objectArray1 = new object[1];
        private object[] _objectArray2 = new object[2];

        public CustomVendingSetup()
        {
            _monumentFinderAdapter = new MonumentFinderAdapter(this);
            _itemRetrieverAdapter = new ItemRetrieverAdapter(this);
            _componentFactory = new ComponentFactory<NPCVendingMachine, VendingMachineComponent>(this, _componentTracker);
            _vendingMachineManager = new VendingMachineManager(this, _componentFactory, _dataProviderRegistry);
            _bagOfHoldingLimitManager = new BagOfHoldingLimitManager(this);
            _paymentProviderResolver = new PaymentProviderResolver(this);
            _inaccessibleVendingMachines = new DynamicHookSubscriber<VendingController>(this, nameof(CanAccessVendingMachine));
            _playersNeedingFakeInventory = new DynamicHookSubscriber<BasePlayer>(this, nameof(OnEntitySaved), nameof(OnInventoryNetworkUpdate));
        }

        #endregion

        #region Hooks

        private void Init()
        {
            _config.Init();
            _prefabRelativeData = SavedPrefabRelativeData.Load();
            _salesData = SavedSalesData.Load();

            permission.RegisterPermission(PermissionUse, this);

            Unsubscribe(nameof(OnEntitySpawned));

            _inaccessibleVendingMachines.UnsubscribeAll();
            _playersNeedingFakeInventory.UnsubscribeAll();
        }

        private void OnServerInitialized()
        {
            _isServerInitialized = true;
            _mapData = SavedMapData.Load();

            if (MonumentFinder == null)
            {
                LogWarning("MonumentFinder is not loaded, so you won't be able to save vending machine customizations relative to monuments.");
            }

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
            if (NPCVendingMachine.DynamicPricingEnabled)
            {
                _vendingMachineManager.SaveAllSalesData();
            }

            _vendingMachineManager.ResetAll();
            ObjectCache.Clear<int>();
            ObjectCache.Clear<float>();
            ObjectCache.Clear<ulong>();
        }

        private void OnNewSave()
        {
            if (NPCVendingMachine.DynamicPricingEnabled)
            {
                _salesData.Reset();
            }
        }

        private void OnServerSave()
        {
            if (NPCVendingMachine.DynamicPricingEnabled)
            {
                _vendingMachineManager.SaveAllSalesData();
            }
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

            var profile = controller.Profile;
            if (profile?.Offers == null)
                return;

            if (_config.ShopUISettings.EnableSkinOverlays)
            {
                component.ShowShopUI(player);
            }

            if ((_config.Economics.EnabledAndValid && profile.HasPaymentProviderCurrency(_config.Economics))
                || (_config.ServerRewards.EnabledAndValid && profile.HasPaymentProviderCurrency(_config.ServerRewards)))
            {
                // Make sure OnEntitySaved/OnInventoryNetworkUpdate are subscribed to modify network updates.
                _playersNeedingFakeInventory.Add(player);

                // Mark inventory dirty to send a network update, which will be modified by hooks.
                player.inventory.containerMain.MarkDirty();
            }
        }

        private object OnVendingTransaction(NPCVendingMachine vendingMachine, BasePlayer player, int sellOrderIndex, int numberOfTransactions, ItemContainer targetContainer)
        {
            var vendingProfile = _vendingMachineManager.GetController(vendingMachine)?.Profile;
            if (vendingProfile?.Offers == null)
            {
                // Don't override the transaction logic because the vending machine is not customized by this plugin.
                return null;
            }

            var component = _componentTracker.GetComponent(vendingMachine);
            if (component == null)
                return null;

            var offer = vendingProfile.GetOfferForSellOrderIndex(sellOrderIndex);
            if (offer == null)
            {
                // Something is wrong. No valid offer exists at the specified index.
                return null;
            }

            numberOfTransactions = Mathf.Clamp(numberOfTransactions, 1, HasCondition(offer.SellItem.ItemDefinition) ? 1 : 1000000);

            var sellAmount = offer.SellItem.Amount * numberOfTransactions;
            var sellOrder = vendingMachine.sellOrders.sellOrders[sellOrderIndex];
            if (offer.SellItem.ItemDefinition == NPCVendingMachine.ScrapItem && sellOrder.receivedQuantityMultiplier != 1f)
            {
                // Modify the amount of scrap received according to dynamic pricing.
                sellAmount = GetTotalPriceForOrder(sellAmount, sellOrder.receivedQuantityMultiplier);
            }

            var sellItemQuery = ItemQuery.FromSellItem(offer.SellItem);
            if (ItemUtils.SumContainerItems(vendingMachine.inventory, ref sellItemQuery) < sellAmount)
            {
                // The vending machine has insufficient stock.
                return False;
            }

            var currencyAmount = GetTotalPriceForOrder(offer.CurrencyItem.Amount, sellOrder.priceMultiplier) * numberOfTransactions;
            var currencyProvider = _paymentProviderResolver.Resolve(offer.CurrencyItem);
            if (currencyProvider.GetBalance(player) < currencyAmount)
            {
                // The player has insufficient currency.
                return False;
            }

            _reusableItemList.Clear();
            currencyProvider.TakeBalance(player, currencyAmount, _reusableItemList);

            var onMarketplaceItemPurchase = (targetContainer?.entityOwner as MarketTerminal)?._onItemPurchasedCached;

            // Note: The list will be empty if Economics or Server Rewards currency were used.
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

            if (offer.RefillDelay <= 0)
            {
                // Don't change the stock amount. Instead, we will just leave the items in the vending machine.
                // The "CanVendingStockRefill" hook will use this flag to skip all logic.
                _performingInstantRestock = true;
            }
            else
            {
                // The "CanVendingStockRefill" hook may use this to add stock.
                _itemBeingSold = offer.SellItem;
            }

            _paymentProviderResolver.Resolve(offer.SellItem).AddBalance(player, sellAmount, new TransactionContext
            {
                VendingMachine = vendingMachine,
                SellItem = offer.SellItem,
                TargetContainer = targetContainer,
                OnMarketplaceItemPurchase = onMarketplaceItemPurchase,
            });

            vendingMachine.RecordSale(sellOrderIndex, sellAmount, offer.CurrencyItem.Amount * numberOfTransactions);

            // These can now be unset since the "CanVendingStockRefill" hook can no longer be called after this point.
            _performingInstantRestock = false;
            _itemBeingSold = null;

            if (offer.RefillDelay > 0)
            {
                // Remove stock only after the items have been given to the player,
                // so that max stack size can be determined by an item in stock.
                ItemUtils.TakeContainerItems(vendingMachine.inventory, ref sellItemQuery, sellAmount);
            }

            vendingMachine.UpdateEmptyFlag();

            // Reopen the UI if it was closed due to a transaction delay.
            if (!component.HasUI(player) && IsLootingVendingMachine(player, vendingMachine))
            {
                OnVendingShopOpened(vendingMachine, player);
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

            var component = _componentTracker.GetComponent(vendingMachine);
            if (component == null)
                return;

            ScheduleRemoveUI(vendingMachine, player, component);
        }

        private object OnNpcGiveSoldItem(NPCVendingMachine vendingMachine, Item item, BasePlayer player)
        {
            if (!IsCustomized(vendingMachine))
                return null;

            ExposedHooks.OnCustomVendingSetupGiveSoldItem(vendingMachine, item, player);

            // Simply give the item, without splitting it, since stack size logic has already been taken into account.
            player.GiveItem(item);
            return False;
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

        private void OnEntitySaved(BasePlayer player, BaseNetworkable.SaveInfo saveInfo)
        {
            AddCurrencyToContainerSnapshot(player, saveInfo.msg.basePlayer.inventory.invMain);
        }

        private void OnInventoryNetworkUpdate(PlayerInventory inventory, ItemContainer container, ProtoBuf.UpdateItemContainer updatedItemContainer, PlayerInventory.Type inventoryType)
        {
            if (inventoryType != PlayerInventory.Type.Main)
                return;

            AddCurrencyToContainerSnapshot(inventory.baseEntity, updatedItemContainer.container[0]);
        }

        #endregion

        #region API



        [HookMethod(nameof(API_IsCustomized))]
        public object API_IsCustomized(NPCVendingMachine vendingMachine)
        {
            return IsCustomized(vendingMachine) ? True : False;
        }

        [HookMethod(nameof(API_RefreshDataProvider))]
        public void API_RefreshDataProvider(NPCVendingMachine vendingMachine)
        {
            _vendingMachineManager.RefreshDataProvider(vendingMachine);
        }

        // Undocumented. Intended for MonumentAddons migration to become a Data Provider.
        [HookMethod(nameof(API_MigrateVendingProfile))]
        public JObject API_MigrateVendingProfile(NPCVendingMachine vendingMachine)
        {
            if (PrefabRelativePosition.FromVendingMachine(_monumentFinderAdapter, vendingMachine) is not {} location)
            {
                // This can happen if a vending machine was moved outside a monument's bounds.
                return null;
            }

            var vendingProfile = _prefabRelativeData.FindProfile(location);
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

            _prefabRelativeData.VendingProfiles.Remove(vendingProfile);
            _prefabRelativeData.Save();

            return jObject;
        }

        #endregion

        #region Dependencies

        private class MonumentAdapter
        {
            public string PrefabName => (string)_monumentInfo["PrefabName"];
            public string ShortName => (string)_monumentInfo["ShortName"];
            public string Alias => (string)_monumentInfo["Alias"];
            public Vector3 Position => (Vector3)_monumentInfo["Position"];

            private Dictionary<string, object> _monumentInfo;

            public MonumentAdapter(Dictionary<string, object> monumentInfo)
            {
                _monumentInfo = monumentInfo;
            }

            public Vector3 InverseTransformPoint(Vector3 worldPosition)
            {
                return ((Func<Vector3, Vector3>)_monumentInfo["InverseTransformPoint"]).Invoke(worldPosition);
            }

            public bool IsInBounds(Vector3 position)
            {
                return ((Func<Vector3, bool>)_monumentInfo["IsInBounds"]).Invoke(position);
            }
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
                if (_monumentFinder?.Call("API_GetClosest", position) is not Dictionary<string, object> dictResult)
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
                if (result is not true)
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

            public static void OnCustomVendingSetupGiveSoldItem(NPCVendingMachine vendingMachine, Item item, BasePlayer player)
            {
                Interface.CallHook("OnCustomVendingSetupGiveSoldItem", vendingMachine, item, player);
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

            if (!PassesUICommandChecks(player, args, out var vendingMachine, out var vendingController))
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

                    // Allow Map data provider to be replaced with a Monument data provider.
                    if (vendingController.DataProvider is MapDataProvider)
                    {
                        _vendingMachineManager.RefreshDataProvider(vendingMachine);
                    }

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

        private static bool IsLootingVendingMachine(BasePlayer player, NPCVendingMachine vendingMachine)
        {
            return player.inventory.loot.containers.FirstOrDefault()?.entityOwner == vendingMachine;
        }

        private static bool AreVectorsClose(Vector3 a, Vector3 b, float xZTolerance = 0.01f, float yTolerance = 10)
        {
            // Allow a generous amount of vertical distance given that plugins may snap entities to terrain.
            return Math.Abs(a.y - b.y) < yTolerance
                && Math.Abs(a.x - b.x) < xZTolerance
                && Math.Abs(a.z - b.z) < xZTolerance;
        }

        private static bool HasCondition(ItemDefinition itemDefinition)
        {
            return itemDefinition.condition is { enabled: true, max: > 0 };
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

        private static bool CanVendingMachineBeSkinned(NPCVendingMachine vendingMachine)
        {
            return vendingMachine is not InvisibleVendingMachine
                && vendingMachine.GetParentEntity() is not TravellingVendor;
        }

        private static bool CanVendingMachineBroadcast(NPCVendingMachine vendingMachine)
        {
            return vendingMachine.GetParentEntity() is not TravellingVendor;
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

        private static StorageContainer CreateOrdersContainer(CustomVendingSetup plugin, NPCVendingMachine vendingMachine, BasePlayer player, VendingOffer[] vendingOffers)
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
                {
                    settingsItem.Remove();
                }
            }

            var generalSettingsItem = ItemManager.Create(plugin._noteItemDefinition);
            if (generalSettingsItem != null)
            {
                var settingsMap = new CaseInsensitiveDictionary<string>();

                if (NPCVendingMachine.DynamicPricingEnabled)
                {
                    var dynamicPricingLabel = plugin.GetMessage(player, Lang.SettingsBypassDynamicPricing);
                    settingsMap[dynamicPricingLabel] = vendingMachine.BypassDynamicPricing.ToString();
                }

                if (CanVendingMachineBeSkinned(vendingMachine))
                {
                    var skinIdLabel = plugin.GetMessage(player, Lang.SettingsSkinId);
                    settingsMap[skinIdLabel] = vendingMachine.skinID.ToString();
                }

                var shopNameLabel = plugin.GetMessage(player, Lang.SettingsShopName);
                settingsMap[shopNameLabel] = vendingMachine.shopName;

                generalSettingsItem.text = CreateNoteContents(settingsMap);
                if (!generalSettingsItem.MoveToContainer(container, GeneralSettingsNoteSlot))
                {
                    generalSettingsItem.Remove();
                }
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

        private static void GiveSoldItem(Item item, BasePlayer player, ref TransactionContext transaction)
        {
            var vendingMachine = transaction.VendingMachine;
            var targetContainer = transaction.TargetContainer;

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

            transaction.OnMarketplaceItemPurchase?.Invoke(player, item);
        }

        private static int GetHighestUsedSlot(ProtoBuf.ItemContainer containerData)
        {
            var highestUsedSlot = -1;

            foreach (var item in containerData.contents)
            {
                if (item.slot > highestUsedSlot)
                {
                    highestUsedSlot = item.slot;
                }
            }

            return highestUsedSlot;
        }

        private static void AddItemForNetwork(ProtoBuf.ItemContainer containerData, int slot, int itemId, int amount, ItemId uid)
        {
            var itemData = Pool.Get<ProtoBuf.Item>();
            itemData.slot = slot;
            itemData.itemid = itemId;
            itemData.amount = amount;
            itemData.UID = uid;
            containerData.contents.Add(itemData);
        }

        private static CaseInsensitiveDictionary<string> ParseSettings(string text)
        {
            var dict = new CaseInsensitiveDictionary<string>();
            if (string.IsNullOrEmpty(text))
                return dict;

            foreach (Match match in KeyValueRegex.Matches(text))
            {
                dict[match.Groups[1].Value.Trim()] = match.Groups[2].Value.Trim();
            }

            return dict;
        }

        private object CallPlugin<T1>(Plugin plugin, string methodName, T1 arg1)
        {
            _objectArray1[0] = ObjectCache.Get(arg1);
            return plugin.Call(methodName, _objectArray1);
        }

        private object CallPlugin<T1, T2>(Plugin plugin, string methodName, T1 arg1, T2 arg2)
        {
            _objectArray2[0] = ObjectCache.Get(arg1);
            _objectArray2[1] = ObjectCache.Get(arg2);
            return plugin.Call(methodName, _objectArray2);
        }

        private void ScheduleRemoveUI(NPCVendingMachine vendingMachine, BasePlayer player, VendingMachineComponent component)
        {
            component.Invoke(() =>
            {
                if (vendingMachine == null || vendingMachine.IsDestroyed)
                    return;

                if (IsLootingVendingMachine(player, vendingMachine) &&
                    !vendingMachine.IsInvoking(vendingMachine.CompletePendingOrder))
                    return;

                // Remove the UI because the player stopped viewing the vending machine or the transaction is pending.
                component.RemoveUI(player);
            }, 0);
        }

        private void AddCurrencyToContainerSnapshot(BasePlayer player, ProtoBuf.ItemContainer containerData)
        {
            if (containerData == null
                || containerData.slots < InventorySize
                || !_playersNeedingFakeInventory.Contains(player))
                return;

            var lootingContainer = player.inventory.loot.containers.FirstOrDefault();
            var vendingMachine = lootingContainer?.entityOwner as NPCVendingMachine;
            if ((object)vendingMachine == null)
                return;

            var profile = _componentTracker.GetComponent(vendingMachine)?.Profile;
            if (profile == null)
                return;

            var nextInvisibleSlot = Math.Max(containerData.slots, GetHighestUsedSlot(containerData) + 1);

            if (_config.Economics.EnabledAndValid && profile.HasPaymentProviderCurrency(_config.Economics))
            {
                AddItemForNetwork(
                    containerData,
                    slot: nextInvisibleSlot,
                    itemId: _config.Economics.ItemDefinition.itemid,
                    amount: _paymentProviderResolver.EconomicsPaymentProvider.GetBalance(player),
                    uid: new ItemId(ulong.MaxValue - (ulong)nextInvisibleSlot)
                );
                nextInvisibleSlot++;
            }

            if (_config.ServerRewards.EnabledAndValid && profile.HasPaymentProviderCurrency(_config.ServerRewards))
            {
                AddItemForNetwork(
                    containerData,
                    slot: nextInvisibleSlot,
                    itemId: _config.ServerRewards.ItemDefinition.itemid,
                    amount: _paymentProviderResolver.ServerRewardsPaymentProvider.GetBalance(player),
                    uid: new ItemId(ulong.MaxValue - (ulong)nextInvisibleSlot)
                );
                nextInvisibleSlot++;
            }

            containerData.slots = nextInvisibleSlot;
        }

        private Dictionary<string, object> SetupItemRetrieverQuery(ref ItemQuery itemQuery)
        {
            _itemRetrieverQuery.Clear();
            _itemRetrieverQuery["MinCondition"] = ObjectCache.Get(MinCurrencyCondition);
            _itemRetrieverQuery["RequireEmpty"] = True;

            if (itemQuery.BlueprintId != 0)
            {
                _itemRetrieverQuery["BlueprintId"] = ObjectCache.Get(itemQuery.BlueprintId);
            }

            if (itemQuery.DataInt != 0)
            {
                _itemRetrieverQuery["DataInt"] = ObjectCache.Get(itemQuery.DataInt);
            }

            if (itemQuery.ItemId != 0)
            {
                _itemRetrieverQuery["ItemId"] = ObjectCache.Get(itemQuery.ItemId);
            }

            if (itemQuery.SkinId.HasValue)
            {
                _itemRetrieverQuery["SkinId"] = ObjectCache.Get(itemQuery.SkinId.Value);
            }

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

        private bool PassesUICommandChecks(IPlayer player, string[] args, out NPCVendingMachine vendingMachine, out VendingController controller)
        {
            vendingMachine = null;
            controller = null;

            if (player.IsServer || !player.HasPermission(PermissionUse))
                return false;

            if (args.Length == 0 || !ulong.TryParse(args[0], out var vendingMachineId))
                return false;

            vendingMachine = BaseNetworkable.serverEntities.Find(new NetworkableId(vendingMachineId)) as NPCVendingMachine;
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
            public static EditFormState FromVendingMachine(VendingController vendingController, NPCVendingMachine vendingMachine)
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

            public static string RenderContainerUI(CustomVendingSetup plugin, BasePlayer player, NPCVendingMachine vendingMachine, VendingController controller, EditFormState uiState)
            {
                var offsetX = 192;
                var offsetY = 142.5f;

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
                    },
                };

                var saveButtonText = plugin.GetMessage(player, Lang.ButtonSave);
                var cancelButtonText = plugin.GetMessage(player, Lang.ButtonCancel);

                var vendingMachineId = vendingMachine.net.ID.Value;

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

                if (CanVendingMachineBroadcast(vendingMachine))
                {
                    AddBroadcastButton(cuiElements, vendingMachine, uiState);
                    AddDroneButton(cuiElements, vendingMachine, uiState);
                }

                AddDataProviderInfo(cuiElements, controller.DataProvider switch
                {
                    MapDataProvider => plugin.GetMessage(player, Lang.InfoDataProviderMap, SavedMapData.GetMapName()),
                    PrefabRelativeDataProvider prefabRelativeDataProvider => plugin.GetMessage(player, prefabRelativeDataProvider.Location.GetDataProviderLabel(), prefabRelativeDataProvider.Location.GetShortName()),
                    PluginDataProvider pluginDataProvider => pluginDataProvider.Plugin != null
                        ? plugin.GetMessage(player, Lang.InfoDataProviderPlugin, pluginDataProvider.Plugin.Name)
                        : plugin.GetMessage(player, Lang.InfoDataProviderPluginUnknownName),
                    _ => "",
                });

                var headerOffset = -6;

                cuiElements.Add(new CuiElement
                {
                    Parent = UIName,
                    Name = TipUIName,
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
                        },
                    },
                });

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

            private static void AddDataProviderInfo(CuiElementContainer cuiElements, string text)
            {
                var xMax = UIConstants.PanelWidth;
                var xMin = 0;

                var textHeight = 14;
                var padding = 2;

                cuiElements.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = text,
                            Color = "1 1 1 1",
                            Align = TextAnchor.LowerRight,
                            FontSize = 10,
                            Font = "RobotoCondensed-Regular.ttf",
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "0 0",
                            OffsetMin = $"{xMin} {UIConstants.ButtonHeight + padding}",
                            OffsetMax = $"{xMax} {UIConstants.ButtonHeight + padding + textHeight}",
                        },
                    },
                    UIName
                );
            }

            private static void AddHeaderLabel(CuiElementContainer cuiElements, int index, string text)
            {
                var xMin = 6 + index * (UIConstants.ItemBoxSize + UIConstants.ItemSpacing);
                var xMax = xMin + UIConstants.ItemBoxSize;

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
                        },
                    },
                    TipUIName
                );
            }

            private static void AddBroadcastButton(CuiElementContainer cuiElements, NPCVendingMachine vendingMachine, EditFormState uiState)
            {
                var iconSize = UIConstants.ButtonHeight;

                var xMax = UIConstants.PanelWidth - 2 * (UIConstants.ButtonWidth + UIConstants.ButtonHorizontalSpacing);
                var xMin = xMax - iconSize;

                cuiElements.Add(new CuiElement
                {
                    Parent = UIName,
                    Name = BroadcastUIName,
                    DestroyUi = BroadcastUIName,
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
                });

                cuiElements.Add(new CuiElement
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
                });
            }

            private static void AddDroneButton(CuiElementContainer cuiElements, NPCVendingMachine vendingMachine, EditFormState uiState)
            {
                var iconSize = UIConstants.ButtonHeight;

                var xMax = - UIConstants.ButtonHorizontalSpacing;
                var xMin = xMax - iconSize;

                var droneAccessible = uiState.Broadcast && uiState.DroneAccessible;

                cuiElements.Add(new CuiElement
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
                });

                cuiElements.Add(new CuiElement
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
                });
            }

            public static string RenderBroadcastUI(NPCVendingMachine vendingMachine, EditFormState uiState)
            {
                var cuiElements = new CuiElementContainer();
                AddBroadcastButton(cuiElements, vendingMachine, uiState);
                AddDroneButton(cuiElements, vendingMachine, uiState);
                return CuiHelper.ToJson(cuiElements);
            }

            private static void AddButton(CuiElementContainer cuiElements, ulong vendingMachineId, string text, string subCommand, float xMax, string color, string textColor)
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
                var offsetY = 137.5f + 74 * numSellOrders;
                var offsetX = 192;

                var cuiElements = new CuiElementContainer
                {
                    new CuiElement
                    {
                        Parent = "Overlay",
                        Name = UIName,
                        DestroyUi = UIName,
                        Components =
                        {
                            new CuiRectTransformComponent
                            {
                                AnchorMin = UIConstants.AnchorMin,
                                AnchorMax = UIConstants.AnchorMax,
                                OffsetMin = $"{offsetX} {offsetY}",
                                OffsetMax = $"{offsetX} {offsetY}",
                            },
                        },
                    },
                };

                var buttonIndex = 0;
                var vendingMachineId = vendingMachine.net.ID.Value;

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

            private static void AddVendingButton(CuiElementContainer cuiElements, ulong vendingMachineId, string text, string subCommand, int reverseButtonIndex, string color, string textColor)
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
                    new CuiElement
                    {
                        Parent = "Hud.Menu",
                        Name = UIName,
                        DestroyUi = UIName,
                        Components =
                        {
                            new CuiRectTransformComponent
                            {
                                AnchorMin = UIConstants.AnchorMin,
                                AnchorMax = UIConstants.AnchorMax,
                            },
                        },
                    },
                };

                var skinsByItemShortName = new Dictionary<string, HashSet<ulong>>();
                var numValidOffers = 0;

                foreach (var offer in vendingProfile.Offers)
                {
                    if (!offer.IsValid)
                        continue;

                    numValidOffers++;

                    if (!skinsByItemShortName.TryGetValue(offer.SellItem.ShortName, out var skins))
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
                var offsetY = 41.5f + 74 * indexFromBottom;

                var vendingItem = isCurrency ? offer.CurrencyItem : offer.SellItem;

                // Background
                cuiElements.Add(new CuiElement
                {
                    Parent = UIName,
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
                        },
                    },
                });

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
                            OffsetMax = $"{offsetX + PaddingLeft + IconSize} {offsetY + PaddingBottom + IconSize}",
                        },
                    },
                });

                if (vendingItem.Amount > 1)
                {
                    // Amount
                    cuiElements.Add(new CuiElement
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
                            },
                        },
                    });
                }
            }
        }

        #endregion

        #region Utilities

        private static class StringUtils
        {
            public static bool EqualsCaseInsensitive(string a, string b)
            {
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) == 0;
            }
        }

        private static class ObjectCache
        {
            private static class StaticObjectCache<T>
            {
                private static readonly Dictionary<T, object> _cacheByValue = new();

                public static object Get(T value)
                {
                    if (!_cacheByValue.TryGetValue(value, out var cachedObject))
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

        private interface IRelativePosition
        {
            string GetPrefabName();
            string GetPrefabAlias();
            Vector3 GetPosition();
        }

        private static bool LocationsMatch<T1, T2>(T1 a, T2 b)
            where T1 : IRelativePosition
            where T2 : IRelativePosition
        {
            var prefabsMatch = a.GetPrefabAlias() != null && a.GetPrefabAlias() == b.GetPrefabAlias()
                || StringUtils.EqualsCaseInsensitive(a.GetPrefabName(), b.GetPrefabName());

            if (!prefabsMatch)
                return false;

            return AreVectorsClose(a.GetPosition(), b.GetPosition());
        }

        private struct PrefabRelativePosition : IRelativePosition
        {
            public static PrefabRelativePosition? FromVendingMachine(MonumentFinderAdapter monumentFinderAdapter, NPCVendingMachine vendingMachine)
            {
                var parentEntity = vendingMachine.GetParentEntity();
                if (parentEntity != null)
                {
                    return new PrefabRelativePosition
                    {
                        _vendingMachine = vendingMachine,
                        _parentEntity = parentEntity,
                        _position = vendingMachine.transform.localPosition,
                    };
                }

                var monument = monumentFinderAdapter.GetMonumentAdapter(vendingMachine);
                if (monument == null)
                    return null;

                return new PrefabRelativePosition
                {
                    _vendingMachine = vendingMachine,
                    _monumentAdapter = monument,
                    _position = monument.InverseTransformPoint(vendingMachine.transform.position),
                };
            }

            private NPCVendingMachine _vendingMachine;
            private BaseEntity _parentEntity;
            private MonumentAdapter _monumentAdapter;
            private Vector3 _position;

            public string GetShortName()
            {
                return _monumentAdapter != null
                    ? _monumentAdapter.ShortName
                    : _parentEntity.ShortPrefabName;
            }

            public Vector3 GetCurrentPosition()
            {
                return _monumentAdapter?.InverseTransformPoint(_vendingMachine.transform.position)
                       ?? _vendingMachine.transform.localPosition;
            }

            public string GetDataProviderLabel()
            {
                return _monumentAdapter != null
                    ? Lang.InfoDataProviderMonument
                    : Lang.InfoDataProviderEntity;
            }

            // IPrefabRelativePosition members.
            public string GetPrefabName() => _monumentAdapter != null
                ? _monumentAdapter.PrefabName
                : _parentEntity.PrefabName;

            public string GetPrefabAlias() => _monumentAdapter?.Alias;
            public Vector3 GetPosition() => _position;
        }

        #endregion

        #region Payment Providers

        private struct TransactionContext
        {
            public NPCVendingMachine VendingMachine;
            public VendingItem SellItem;
            public ItemContainer TargetContainer;
            public Action<BasePlayer, Item> OnMarketplaceItemPurchase;
        }

        private interface IPaymentProvider
        {
            int GetBalance(BasePlayer player);
            bool AddBalance(BasePlayer player, int amount, TransactionContext transaction);
            bool TakeBalance(BasePlayer player, int amount, List<Item> collect);
        }

        private class ItemsPaymentProvider : IPaymentProvider
        {
            public VendingItem VendingItem;

            private CustomVendingSetup _plugin;

            public ItemsPaymentProvider(CustomVendingSetup plugin)
            {
                _plugin = plugin;
            }

            public int GetBalance(BasePlayer player)
            {
                var itemQuery = ItemQuery.FromCurrencyItem(VendingItem);
                return _plugin.SumPlayerItems(player, ref itemQuery);
            }

            public bool AddBalance(BasePlayer player, int amount, TransactionContext transaction)
            {
                var vendingMachine = transaction.VendingMachine;
                var sellItem = transaction.SellItem;

                var sellItemQuery = ItemQuery.FromSellItem(sellItem);
                var firstSellableItem = ItemUtils.FindFirstContainerItem(vendingMachine.inventory, ref sellItemQuery);
                var maxStackSize = _plugin._config.GetItemMaxStackSize(firstSellableItem);

                // Create new items and give them to the player.
                // This approach was chosen instead of transferring the items because in many cases new items would have to
                // be created anyway, since the vending machine maintains a single large stack of each item.
                while (amount > 0)
                {
                    var amountToGive = Math.Min(amount, maxStackSize);
                    var itemToGive = sellItem.Create(amountToGive);

                    amount -= amountToGive;

                    // The "CanPurchaseItem" hook may cause "CanVendingStockRefill" hook to be called.
                    var hookResult = ExposedHooks.CanPurchaseItem(player, itemToGive, transaction.OnMarketplaceItemPurchase, vendingMachine, transaction.TargetContainer);
                    if (hookResult is bool)
                    {
                        LogWarning($"A plugin returned {hookResult} in the CanPurchaseItem hook, which has been ignored.");
                    }

                    GiveSoldItem(itemToGive, player, ref transaction);
                }

                return true;
            }

            public bool TakeBalance(BasePlayer player, int amount, List<Item> collect)
            {
                if (amount <= 0)
                    return true;

                var itemQuery = ItemQuery.FromCurrencyItem(VendingItem);
                _plugin.TakePlayerItems(player, ref itemQuery, amount, collect);
                return true;
            }
        }

        private class EconomicsPaymentProvider : IPaymentProvider
        {
            private CustomVendingSetup _plugin;
            private Plugin _ownerPlugin => _plugin.Economics;

            public EconomicsPaymentProvider(CustomVendingSetup plugin)
            {
                _plugin = plugin;
            }

            public bool IsAvailable => _ownerPlugin != null;

            public int GetBalance(BasePlayer player)
            {
                return Convert.ToInt32(_plugin.CallPlugin(_ownerPlugin, "Balance", (ulong)player.userID));
            }

            public bool AddBalance(BasePlayer player, int amount, TransactionContext transaction)
            {
                var result = _plugin.CallPlugin(_ownerPlugin, "Deposit", (ulong)player.userID, Convert.ToDouble(amount));
                return result is true;
            }

            public bool TakeBalance(BasePlayer player, int amount, List<Item> collect)
            {
                var result = _plugin.CallPlugin(_ownerPlugin, "Withdraw", (ulong)player.userID, Convert.ToDouble(amount));
                return result is true;
            }
        }

        private class ServerRewardsPaymentProvider : IPaymentProvider
        {
            private CustomVendingSetup _plugin;
            private Plugin _ownerPlugin => _plugin.ServerRewards;

            public ServerRewardsPaymentProvider(CustomVendingSetup plugin)
            {
                _plugin = plugin;
            }

            public bool IsAvailable => _ownerPlugin != null;

            public int GetBalance(BasePlayer player)
            {
                return Convert.ToInt32(_plugin.CallPlugin(_ownerPlugin, "CheckPoints", (ulong)player.userID));
            }

            public bool AddBalance(BasePlayer player, int amount, TransactionContext transaction)
            {
                var result = _plugin.CallPlugin(_ownerPlugin, "AddPoints", (ulong)player.userID, amount);
                return result is true;
            }

            public bool TakeBalance(BasePlayer player, int amount, List<Item> collect)
            {
                var result = _plugin.CallPlugin(_ownerPlugin, "TakePoints", (ulong)player.userID, amount);
                return result is true;
            }
        }

        private class PaymentProviderResolver
        {
            public readonly EconomicsPaymentProvider EconomicsPaymentProvider;
            public readonly ServerRewardsPaymentProvider ServerRewardsPaymentProvider;

            private readonly CustomVendingSetup _plugin;
            private readonly ItemsPaymentProvider _itemsPaymentProvider;
            private Configuration _config => _plugin._config;

            public PaymentProviderResolver(CustomVendingSetup plugin)
            {
                _plugin = plugin;
                _itemsPaymentProvider = new ItemsPaymentProvider(plugin);
                EconomicsPaymentProvider = new EconomicsPaymentProvider(plugin);
                ServerRewardsPaymentProvider = new ServerRewardsPaymentProvider(plugin);
            }

            public IPaymentProvider Resolve(VendingItem vendingItem)
            {
                if (_config.Economics.MatchesItem(vendingItem) && EconomicsPaymentProvider.IsAvailable)
                    return EconomicsPaymentProvider;

                if (_config.ServerRewards.MatchesItem(vendingItem) && ServerRewardsPaymentProvider.IsAvailable)
                    return ServerRewardsPaymentProvider;

                _itemsPaymentProvider.VendingItem = vendingItem;
                return _itemsPaymentProvider;
            }
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

                if (!string.IsNullOrEmpty(DisplayName) && !StringUtils.EqualsCaseInsensitive(DisplayName, item.name))
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
            private HashSet<T> _list = new();
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

        #region Data Utils

        private interface IDataLoader
        {
            bool Exists(string filename);
            T Load<T>(string filename) where T : new();
            void Save<T>(string filename, T data);
        }

        private class ProtoLoader : IDataLoader
        {
            public bool Exists(string filename)
            {
                return ProtoStorage.Exists(filename);
            }

            public T Load<T>(string filename) where T : new()
            {
                if (Exists(filename))
                    return ProtoStorage.Load<T>(filename) ?? new T();

                return new T();
            }

            public void Save<T>(string filename, T data)
            {
                ProtoStorage.Save(data, filename);
            }
        }

        private class JsonLoader : IDataLoader
        {
            public bool Exists(string filename)
            {
                return Interface.Oxide.DataFileSystem.ExistsDatafile(filename);
            }

            public T Load<T>(string filename) where T : new()
            {
                if (Exists(filename))
                    return Interface.Oxide.DataFileSystem.ReadObject<T>(filename) ?? new T();

                return new T();
            }

            public void Save<T>(string filename, T data)
            {
                Interface.Oxide.DataFileSystem.WriteObject(filename, data);
            }
        }

        private interface IDataLoader<T> where T : new()
        {
            bool Exists();
            T Load();
            void Save(T data);
        }

        private class ProtoLoader<T> : IDataLoader<T> where T : new()
        {
            private readonly ProtoLoader _protoLoader = new();
            private readonly string _filename = null;

            public ProtoLoader(string filename)
            {
                _filename = filename;
            }

            public bool Exists()
            {
                return _protoLoader.Exists(_filename);
            }

            public T Load()
            {
                return _protoLoader.Load<T>(_filename);
            }

            public void Save(T data)
            {
                _protoLoader.Save(_filename, data);
            }
        }

        private class JsonLoader<T> : IDataLoader<T> where T : new()
        {
            private readonly JsonLoader _jsonLoader = new();
            private readonly string _filename = null;

            public JsonLoader(string filename)
            {
                _filename = filename;
            }

            public bool Exists()
            {
                return _jsonLoader.Exists(_filename);
            }

            public T Load()
            {
                return _jsonLoader.Load<T>(_filename);
            }

            public void Save(T data)
            {
                _jsonLoader.Save(_filename, data);
            }
        }

        #endregion

        #region Data Provider

        private interface IDataProvider
        {
            VendingProfile GetData();
            void SaveData(VendingProfile vendingProfile, NPCVendingMachine vendingMachine = null);
        }

        private abstract class DataFileDataProvider : IDataProvider
        {
            private BaseVendingProfileDataFile _dataFile;
            private VendingProfile _vendingProfile;

            protected abstract void BeforeSave(VendingProfile vendingProfile, NPCVendingMachine vendingMachine);

            protected DataFileDataProvider(BaseVendingProfileDataFile dataFile, VendingProfile vendingProfile)
            {
                _dataFile = dataFile;
                _vendingProfile = vendingProfile;
            }

            public VendingProfile GetData()
            {
                return _vendingProfile;
            }

            public void SaveData(VendingProfile vendingProfile, NPCVendingMachine vendingMachine = null)
            {
                if (vendingProfile == null)
                {
                    if (_vendingProfile == null)
                        return;

                    if (!_dataFile.VendingProfiles.Remove(_vendingProfile))
                        return;
                }
                else if (!_dataFile.VendingProfiles.Contains(vendingProfile))
                {
                    _dataFile.VendingProfiles.Add(vendingProfile);
                }

                _vendingProfile = vendingProfile;
                BeforeSave(vendingProfile, vendingMachine);
                _dataFile.Save();
            }
        }

        private class PrefabRelativeDataProvider : DataFileDataProvider
        {
            public PrefabRelativePosition Location;

            public PrefabRelativeDataProvider(SavedPrefabRelativeData prefabRelativeData, PrefabRelativePosition location, VendingProfile vendingProfile)
                : base(prefabRelativeData, vendingProfile)
            {
                Location = location;
            }

            protected override void BeforeSave(VendingProfile vendingProfile, NPCVendingMachine vendingMachine)
            {
                if (vendingProfile == null)
                    return;

                vendingProfile.Monument = Location.GetPrefabName();
                vendingProfile.MonumentAlias = Location.GetPrefabAlias();
                vendingProfile.Position = Location.GetCurrentPosition();
            }
        }

        private class MapDataProvider : DataFileDataProvider
        {
            public MapDataProvider(SavedMapData mapData, VendingProfile vendingProfile)
                : base(mapData, vendingProfile) {}

            protected override void BeforeSave(VendingProfile vendingProfile, NPCVendingMachine vendingMachine)
            {
                if (vendingProfile == null)
                    return;

                // Update the location, in case the vending machine has moved.
                vendingProfile.Position = vendingMachine.transform.position;
            }
        }

        private class PluginDataProvider : IDataProvider
        {
            public static PluginDataProvider FromDictionary(Dictionary<string, object> spec)
            {
                var dataProvider = new PluginDataProvider
                {
                    Spec = spec,
                };

                if (spec.TryGetValue("Plugin", out var plugin))
                {
                    dataProvider.Plugin = plugin as Plugin;
                }

                if (spec.TryGetValue("GetData", out var getDataCallback))
                {
                    dataProvider.GetDataCallback = getDataCallback as CustomGetDataCallback;
                }

                if (spec.TryGetValue("SaveData", out var saveDataCallback))
                {
                    dataProvider.SaveDataCallback = saveDataCallback as CustomSaveDataCallback;
                }

                if (spec.TryGetValue("GetSkin", out var getSkinCallback))
                {
                    dataProvider.GetSkinCallback = getSkinCallback as CustomGetSkinCallback;
                }

                if (spec.TryGetValue("SetSkin", out var setSkinCallback))
                {
                    dataProvider.SetSkinCallback = setSkinCallback as CustomSetSkinCallback;
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
            public Plugin Plugin;
            public CustomGetDataCallback GetDataCallback;
            public CustomSaveDataCallback SaveDataCallback;
            public CustomGetSkinCallback GetSkinCallback;
            public CustomSetSkinCallback SetSkinCallback;

            private VendingProfile _vendingProfile;

            public VendingProfile GetData()
            {
                _vendingProfile ??= GetDataCallback()?.ToObject<VendingProfile>();
                if (_vendingProfile?.Offers == null)
                    return null;

                // DataProvider skin takes precedence if not 0.
                if (GetSkinCallback?.Invoke() is { } skinId && skinId != 0)
                {
                    _vendingProfile.SkinId = skinId;
                }

                return _vendingProfile;
            }

            public void SaveData(VendingProfile vendingProfile, NPCVendingMachine vendingMachine = null)
            {
                var jObject = vendingProfile != null ? JObject.FromObject(vendingProfile) : null;

                if (vendingProfile != null && SetSkinCallback != null)
                {
                    // Inform the Data Provider about the updated skin.
                    SetSkinCallback.Invoke(vendingProfile.SkinId == NpcVendingMachineSkinId ? 0 : vendingProfile.SkinId);

                    // Remove the skin from the full payload, so the Data Provider has only one source of truth.
                    jObject.Remove(VendingProfile.SkinIdField);
                }

                _vendingProfile = vendingProfile;
                SaveDataCallback(jObject);
            }
        }

        private class PluginDataProviderRegistry
        {
            private Dictionary<Dictionary<string, object>, PluginDataProvider> _dataProviderCache = new();

            public PluginDataProvider Register(Dictionary<string, object> dataProviderSpec)
            {
                if (_dataProviderCache.TryGetValue(dataProviderSpec, out var dataProvider))
                    return dataProvider;

                dataProvider = PluginDataProvider.FromDictionary(dataProviderSpec);
                if (dataProvider == null)
                    return null;

                _dataProviderCache[dataProviderSpec] = dataProvider;
                return dataProvider;
            }

            public void Unregister(PluginDataProvider dataProvider)
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
            private PluginDataProviderRegistry _dataProviderRegistry;

            private HashSet<VendingController> _uniqueControllers = new();

            // Controllers are also cached by vending machine, in case MonumentFinder is unloaded or becomes unstable.
            private Dictionary<NetworkableId, VendingController> _controllersByVendingMachine = new();

            private Dictionary<PluginDataProvider, VendingController> _controllersByPluginDataProvider = new();

            private MonumentFinderAdapter _monumentFinderAdapter => _plugin._monumentFinderAdapter;
            private SavedPrefabRelativeData PrefabRelativeData => _plugin._prefabRelativeData;
            private SavedMapData _mapData => _plugin._mapData;
            private SavedSalesData _salesData => _plugin._salesData;

            public VendingMachineManager(CustomVendingSetup plugin, ComponentFactory<NPCVendingMachine, VendingMachineComponent> componentFactory, PluginDataProviderRegistry dataProviderRegistry)
            {
                _plugin = plugin;
                _componentFactory = componentFactory;
                _dataProviderRegistry = dataProviderRegistry;
            }

            public void HandleVendingMachineSpawned(NPCVendingMachine vendingMachine)
            {
                var controller = GetController(vendingMachine);
                if (controller != null)
                {
                    // A controller may already exist if this was called when handling a reload of MonumentFinder.
                    HandleExistingController(vendingMachine, controller);
                    return;
                }

                var hookResult = ExposedHooks.OnCustomVendingSetup(vendingMachine);
                if (hookResult is false)
                    return;

                controller = GetOrCreateController(vendingMachine);
                if (controller == null)
                    return;

                controller.AddVendingMachine(vendingMachine);
                _controllersByVendingMachine[vendingMachine.net.ID] = controller;
            }

            public void HandleVendingMachineKilled(NPCVendingMachine vendingMachine)
            {
                var controller = GetController(vendingMachine);
                if (controller == null)
                    return;

                RemoveFromController(controller, vendingMachine);
            }

            public void RefreshDataProvider(NPCVendingMachine vendingMachine)
            {
                HandleVendingMachineKilled(vendingMachine);
                HandleVendingMachineSpawned(vendingMachine);
            }

            public VendingController GetController(NPCVendingMachine vendingMachine)
            {
                return _controllersByVendingMachine.TryGetValue(vendingMachine.net.ID, out var controller)
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

            public void SaveAllSalesData()
            {
                _salesData.VendingMachines.Clear();

                foreach (var controller in _uniqueControllers)
                {
                    // Only save vending machines which are customized.
                    if (controller.Profile?.Offers == null)
                        continue;

                    foreach (var vendingMachine in controller.VendingMachineList)
                    {
                        // Only save vending machines which have dynamic pricing enabled.
                        if (vendingMachine.BypassDynamicPricing)
                            continue;

                        _salesData.VendingMachines.Add(VendingMachineState.FromVendingMachine(vendingMachine));
                    }
                }

                _salesData.Save();
            }

            private VendingController FindPrefabRelativeController(PrefabRelativePosition location)
            {
                foreach (var controller in _uniqueControllers)
                {
                    if (controller.DataProvider is not PrefabRelativeDataProvider relativeDataProvider)
                        continue;

                    if (LocationsMatch(relativeDataProvider.Location, location))
                        return controller;
                }

                return null;
            }

            private VendingController GetControllerByPluginDataProvider(PluginDataProvider dataProvider)
            {
                return _controllersByPluginDataProvider.TryGetValue(dataProvider, out var controller)
                    ? controller
                    : null;
            }

            private VendingController CreateController(IDataProvider dataProvider)
            {
                var controller = new VendingController(_plugin, _componentFactory, dataProvider);
                _uniqueControllers.Add(controller);
                return controller;
            }

            private void AddToController(VendingController controller, NPCVendingMachine vendingMachine)
            {
                controller.AddVendingMachine(vendingMachine);
                _controllersByVendingMachine[vendingMachine.net.ID] = controller;
            }

            private void RemoveFromController(VendingController controller, NPCVendingMachine vendingMachine)
            {
                controller.RemoveVendingMachine(vendingMachine);
                _controllersByVendingMachine.Remove(vendingMachine.net.ID);

                if (controller.HasVendingMachines)
                    return;

                _uniqueControllers.Remove(controller);

                if (controller.DataProvider is PluginDataProvider dataProvider)
                {
                    _controllersByPluginDataProvider.Remove(dataProvider);
                    _dataProviderRegistry.Unregister(dataProvider);
                }
            }

            private VendingController CreatePrefabRelativeController(PrefabRelativePosition location)
            {
                return CreateController(new PrefabRelativeDataProvider(PrefabRelativeData, location, PrefabRelativeData.FindProfile(location)));
            }

            private void HandleExistingController(NPCVendingMachine vendingMachine, VendingController controller)
            {
                // Only replace a controller if it's a map data provider without existing data.
                if (controller.DataProvider is not MapDataProvider || controller.DataProvider.GetData() != null)
                    return;

                // Keep using the existing map controller and data provider if not prefab-relative eligible.
                if (PrefabRelativePosition.FromVendingMachine(_monumentFinderAdapter, vendingMachine) is not { } location)
                    return;

                // Replace the map controller with a prefab-relative controller.
                RemoveFromController(controller, vendingMachine);
                AddToController(FindPrefabRelativeController(location) ?? CreatePrefabRelativeController(location), vendingMachine);
            }

            private MapDataProvider CreateMapDataProvider(VendingProfile vendingProfile = null)
            {
                return new MapDataProvider(_mapData, vendingProfile);
            }

            private VendingController GetOrCreateController(NPCVendingMachine vendingMachine)
            {
                // Check if another plugin wants to take ownership of the vending machine.
                var dataProviderSpec = ExposedHooks.OnCustomVendingSetupDataProvider(vendingMachine);
                if (dataProviderSpec != null)
                {
                    var pluginDataProvider = _dataProviderRegistry.Register(dataProviderSpec);
                    if (pluginDataProvider == null)
                    {
                        // Data provider is invalid.
                        return null;
                    }

                    var pluginController = GetControllerByPluginDataProvider(pluginDataProvider);
                    if (pluginController != null)
                        return pluginController;

                    pluginController = CreateController(pluginDataProvider);
                    _controllersByPluginDataProvider[pluginDataProvider] = pluginController;
                    return pluginController;
                }

                // Use a map data provider if map data exists for this vending machine.
                var vendingProfile = _mapData.FindProfile(vendingMachine.transform.position);
                if (vendingProfile != null)
                    return CreateController(CreateMapDataProvider(vendingProfile));

                // Use a prefab-relative data provider if parented or within a monument.
                if (PrefabRelativePosition.FromVendingMachine(_monumentFinderAdapter, vendingMachine) is { } location)
                    return FindPrefabRelativeController(location) ?? CreatePrefabRelativeController(location);

                // Use a map data provider if not prefab-relative eligible.
                return CreateController(CreateMapDataProvider());
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

            public BasePlayer EditorPlayer { get; }

            private CustomVendingSetup _plugin;
            private VendingController _vendingController;
            private NPCVendingMachine _vendingMachine;
            private StorageContainer _container;
            private EditFormState _formState;

            public EditController(CustomVendingSetup plugin, VendingController vendingController, NPCVendingMachine vendingMachine, BasePlayer editorPlayer)
            {
                _plugin = plugin;
                _vendingController = vendingController;
                _vendingMachine = vendingMachine;
                EditorPlayer = editorPlayer;

                var offers = vendingController.Profile?.Offers ?? GetOffersFromVendingMachine(vendingMachine);

                _container = CreateOrdersContainer(plugin, vendingMachine, editorPlayer, offers);
                _formState = EditFormState.FromVendingMachine(vendingController, vendingMachine);
                EditContainerComponent.AddToContainer(plugin, _container, this);
                _container.SendAsSnapshot(editorPlayer.Connection);
                OpenEditPanel(editorPlayer, _container);

                CuiHelper.AddUi(editorPlayer, ContainerUIRenderer.RenderContainerUI(plugin, editorPlayer, vendingMachine, _vendingController, _formState));
            }

            public void ToggleBroadcast()
            {
                _formState.Broadcast = !_formState.Broadcast;

                CuiHelper.AddUi(EditorPlayer, ContainerUIRenderer.RenderBroadcastUI(_vendingMachine, _formState));
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

                CuiHelper.AddUi(EditorPlayer, ContainerUIRenderer.RenderBroadcastUI(_vendingMachine, _formState));
            }

            public void ApplyStateTo(VendingProfile profile)
            {
                profile.Offers = GetOffersFromContainer(_plugin, EditorPlayer, _container.inventory);
                profile.Broadcast = _formState.Broadcast;
                profile.DroneAccessible = _formState.DroneAccessible;

                var generalSettingsText = _container.inventory.GetSlot(GeneralSettingsNoteSlot)?.text.Trim();

                if (!string.IsNullOrEmpty(generalSettingsText))
                {
                    var settingsDict = ParseSettings(generalSettingsText);

                    if (NPCVendingMachine.DynamicPricingEnabled)
                    {
                        var dynamicPricingEnabledKey = _plugin.GetMessage(EditorPlayer, Lang.SettingsBypassDynamicPricing);
                        if (settingsDict.TryGetValue(dynamicPricingEnabledKey, out var bypassDynamicPricingString)
                            && bool.TryParse(bypassDynamicPricingString, out var bypassDynamicPricing))
                        {
                            profile.BypassDynamicPricing = bypassDynamicPricing;
                        }
                    }

                    if (CanVendingMachineBeSkinned(_vendingMachine))
                    {
                        var skinIdKey = _plugin.GetMessage(EditorPlayer, Lang.SettingsSkinId);
                        if (settingsDict.TryGetValue(skinIdKey, out var skinIdString)
                            && ulong.TryParse(skinIdString, out var skinId))
                        {
                            profile.SkinId = skinId;
                        }
                        else
                        {
                            // Allow the user to revert to vanilla skin by simply removing the option.
                            profile.SkinId = NpcVendingMachineSkinId;
                        }
                    }

                    var shopNameKey = _plugin.GetMessage(EditorPlayer, Lang.SettingsShopName);
                    if (settingsDict.TryGetValue(shopNameKey, out var shopName))
                    {
                        profile.ShopName = shopName;
                    }
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
                    return;

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

        private class VendingController
        {
            public IDataProvider DataProvider { get; }

            // While the Profile is null, the vending machines will be vanilla.
            public VendingProfile Profile => DataProvider.GetData();

            // While the EditController is non-null, a player is editing the vending machine.
            public EditController EditController { get; protected set; }

            public bool HasVendingMachines => VendingMachineList.Count > 0;

            protected CustomVendingSetup _plugin;

            // List of vending machines with a position matching this controller.
            public HashSet<NPCVendingMachine> VendingMachineList = new();

            private ComponentFactory<NPCVendingMachine, VendingMachineComponent> _componentFactory;

            private string _cachedShopUI;

            public VendingController(CustomVendingSetup plugin, ComponentFactory<NPCVendingMachine, VendingMachineComponent> componentFactory, IDataProvider dataProvider)
            {
                _plugin = plugin;
                _componentFactory = componentFactory;
                DataProvider = dataProvider;
                UpdateDroneAccessibility();
            }

            public void StartEditing(BasePlayer player, NPCVendingMachine vendingMachine)
            {
                if (EditController != null)
                    return;

                EditController = new EditController(_plugin, this, vendingMachine, player);
            }

            public void HandleReset()
            {
                DataProvider.SaveData(null);
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
                var profile = Profile ?? VendingProfile.FromVendingMachine(vendingMachine);

                EditController.ApplyStateTo(profile);
                EditController.Kill();

                DataProvider.SaveData(profile, vendingMachine);
                SetupVendingMachines();

                _cachedShopUI = null;

                UpdateDroneAccessibility();
            }

            public void AddVendingMachine(NPCVendingMachine vendingMachine)
            {
                if (!VendingMachineList.Add(vendingMachine))
                    return;

                var component = _componentFactory.GetOrAddTo(vendingMachine);
                component.SetController(this);
                component.SetProfile(Profile);
            }

            public void RemoveVendingMachine(NPCVendingMachine vendingMachine)
            {
                if (!VendingMachineList.Remove(vendingMachine))
                    return;

                if (VendingMachineList.Count == 0)
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
                return _cachedShopUI ??= ShopUIRenderer.RenderShopUI(Profile);
            }

            protected void UpdateDroneAccessibility()
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

            private void SetupVendingMachines()
            {
                foreach (var vendingMachine in VendingMachineList)
                {
                    _componentFactory.GetOrAddTo(vendingMachine).SetProfile(Profile);
                }
            }

            private void ResetVendingMachines()
            {
                foreach (var vendingMachine in VendingMachineList)
                {
                    VendingMachineComponent.RemoveFromVendingMachine(vendingMachine);
                }
            }
        }

        #endregion

        #region Component Tracker & Factory

        private class ComponentTracker<THost, TGuest>
            where THost : UnityEngine.Component
            where TGuest : UnityEngine.Component
        {
            private readonly Dictionary<THost, TGuest> _hostToGuest = new();

            public void RegisterComponent(THost host, TGuest guest)
            {
                _hostToGuest[host] = guest;
            }

            public TGuest GetComponent(THost host)
            {
                return _hostToGuest.TryGetValue(host, out var guest)
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

            private readonly List<BasePlayer> _adminUIViewers = new();
            private readonly List<BasePlayer> _shopUIViewers = new();
            private VendingController _vendingController;
            private NPCVendingMachine _vendingMachine;
            private float[] _refillTimes;

            private string _originalShopName;
            private ulong _originalSkinId;
            private bool _originalBypassDynamicPricing;
            private bool? _originalBroadcast;

            private IDataProvider _dataProvider => _vendingController.DataProvider;

            public override void OnCreated()
            {
                _vendingMachine = Host;
            }

            public bool HasUI(BasePlayer player)
            {
                return _adminUIViewers.Contains(player) || _shopUIViewers.Contains(player);
            }

            public void ShowAdminUI(BasePlayer player)
            {
                _adminUIViewers.Add(player);
                CuiHelper.AddUi(player, AdminUIRenderer.RenderAdminUI(Plugin, player, _vendingMachine, Profile));
            }

            public void ShowShopUI(BasePlayer player)
            {
                var json = _vendingController.GetShopUI();
                if (json == string.Empty)
                    return;

                _shopUIViewers.Add(player);
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

                // Make sure OnEntitySaved/OnInventoryNetworkUpdate are unsubscribed (when all players are removed).
                Plugin._playersNeedingFakeInventory.Remove(player);

                // Mark inventory dirty to send a network update, which will no longer be modified by hooks.
                player.inventory.containerMain.MarkDirty();
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

            public void SetController(VendingController vendingController)
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

                // Save original values.
                _originalBypassDynamicPricing = _vendingMachine.BypassDynamicPricing;
                _originalSkinId = _vendingMachine.skinID;
                _originalShopName ??= _vendingMachine.shopName;
                _originalBroadcast ??= _vendingMachine.IsBroadcasting();

                // Apply profiles values.
                _vendingMachine.BypassDynamicPricing = profile.BypassDynamicPricing;
                _vendingMachine.skinID = profile.SkinId;

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

                    var vendingOffer = new SellOrder
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

                Plugin._salesData.FindState(_vendingMachine)?.ApplyToVendingMachine(_vendingMachine);
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
                        LogError($"Cannot multiply {refillNumberOfPurchases} by {offer.SellItem.Amount} because the result is too large. You have misconfigured the plugin. It is not necessary to stock that much of any item. Please reduce Max Stock or Refill Amount for item {offer.SellItem.ShortName}.\n" + ex);

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
                            LogError($"Cannot add {refillAmount} to {existingItem.amount} because the result is too large. You have misconfigured the plugin. It is not necessary to stock that much of any item. Please reduce Max Stock or Refill Amount for item {offer.SellItem.ShortName}.\n" + ex);

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

                if (_vendingMachine is InvisibleVendingMachine invisibleVendingMachine)
                {
                    _vendingMachine.CancelInvoke(invisibleVendingMachine.CheckSellOrderRefresh);
                }
            }

            private ulong GetOriginalSkin()
            {
                if ((_dataProvider as PluginDataProvider)?.GetSkinCallback?.Invoke() is { } skinId)
                    return skinId == 0 ? NpcVendingMachineSkinId : skinId;

                return _originalSkinId;
            }

            private void ResetToVanilla()
            {
                CancelInvoke(TimedRefill);

                _vendingMachine.BypassDynamicPricing = _originalBypassDynamicPricing;
                _vendingMachine.skinID = GetOriginalSkin();

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

                if (_vendingMachine is InvisibleVendingMachine { canRefreshOrders: true } invisibleVendingMachine)
                {
                    invisibleVendingMachine.nextOrderRefresh = ConVar.Server.waterWellNpcSalesRefreshFrequency * 60f * 60f;
                    invisibleVendingMachine.InvokeRepeating(invisibleVendingMachine.CheckSellOrderRefresh, 30f, 30f);
                }
            }
        }

        #endregion

        #region Saved Data

        private class CaseInsensitiveDictionary<TValue> : Dictionary<string, TValue>
        {
            public CaseInsensitiveDictionary() : base(StringComparer.OrdinalIgnoreCase) {}

            public CaseInsensitiveDictionary(Dictionary<string, TValue> dict) : base(dict, StringComparer.OrdinalIgnoreCase) {}
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class VendingItem
        {
            public static VendingItem FromItem(Item item)
            {
                var ammoAmount = GetAmmoAmountAndType(item, out var ammoType);

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
            public ItemDefinition ItemDefinition
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

            public bool IsValid => (object)ItemDefinition != null;
            public int ItemId => ItemDefinition.itemid;

            public Item Create(int amount)
            {
                Item item;
                if (IsBlueprint)
                {
                    item = ItemManager.CreateByItemID(BlueprintItemId, amount, SkinId);
                    item.blueprintTarget = ItemDefinition.itemid;
                }
                else
                {
                    item = ItemManager.Create(ItemDefinition, amount, SkinId);
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

                if (Contents is { Count: > 0 })
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

            public VendingItem Copy()
            {
                return new VendingItem
                {
                    ShortName = ShortName,
                    DisplayName = DisplayName,
                    Amount = Amount,
                    SkinId = SkinId,
                    IsBlueprint = IsBlueprint,
                    DataInt = DataInt,
                    Position = Position,
                    AmmoAmount = AmmoAmount,
                    AmmoType = AmmoType,
                    Capacity = Capacity,
                    Contents = Contents,
                };
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

                    var localizedSettings = ParseSettings(settingsItem.text);

                    if (TryParseIntKey(localizedSettings, refillMaxLabel, out var refillMax))
                    {
                        offer.RefillMax = refillMax;
                    }

                    if (TryParseIntKey(localizedSettings, refillDelayLabel, out var refillDelay))
                    {
                        offer.RefillDelay = refillDelay;
                    }

                    if (TryParseIntKey(localizedSettings, refillAmountLabel, out var refillAmount))
                    {
                        offer.RefillAmount = refillAmount;
                    }

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

            private static bool TryParseIntKey(Dictionary<string, string> dict, string key, out int result)
            {
                result = 0;
                return dict.TryGetValue(key, out var stringValue)
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

            public VendingOffer Copy()
            {
                return new VendingOffer
                {
                    SellItem = SellItem.Copy(),
                    CurrencyItem = CurrencyItem.Copy(),
                    RefillMax = RefillMax,
                    RefillDelay = RefillDelay,
                    RefillAmount = RefillAmount,
                    CustomSettings = CustomSettings != null
                        ? new CaseInsensitiveDictionary<object>(CustomSettings)
                        : null,
                };
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class VendingProfile : IRelativePosition
        {
            public const string SkinIdField = "SkinId";

            public static VendingProfile FromVendingMachine(NPCVendingMachine vendingMachine)
            {
                return new VendingProfile
                {
                    SkinId = vendingMachine.skinID,
                    ShopName = vendingMachine.shopName,
                    Broadcast = vendingMachine.IsBroadcasting(),
                };
            }

            [JsonProperty("ShopName", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string ShopName;

            [JsonProperty(SkinIdField, DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(NpcVendingMachineSkinId)]
            public ulong SkinId = NpcVendingMachineSkinId;

            [JsonProperty("BypassDynamicPricing", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool BypassDynamicPricing;

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

            [JsonProperty("Offers")]
            public VendingOffer[] Offers;

            public VendingOffer GetOfferForSellOrderIndex(int index)
            {
                var sellOrderIndex = 0;

                foreach (var offer in Offers)
                {
                    if (!offer.IsValid)
                        continue;

                    if (sellOrderIndex == index)
                        return offer;

                    sellOrderIndex++;
                }

                return null;
            }

            public bool HasPaymentProviderCurrency(PaymentProviderConfig paymentProviderConfig)
            {
                foreach (var offer in Offers)
                {
                    if (paymentProviderConfig.MatchesItem(offer.CurrencyItem))
                        return true;
                }

                return false;
            }

            // IPrefabRelativePosition members.
            public string GetPrefabName() => Monument;
            public string GetPrefabAlias() => MonumentAlias;
            public Vector3 GetPosition() => Position;

            [OnDeserialized]
            private void OnDeserialized(StreamingContext context)
            {
                UpdateOldSaddleOffers();
            }

            private void UpdateOldSaddleOffers()
            {
                if (Offers == null)
                    return;

                VendingOffer singleSaddleOffer = null;
                var singleSaddleIndex = -1;

                for (var i = 0; i < Offers.Length; i++)
                {
                    var offer = Offers[i];
                    if (offer.SellItem.ShortName == "horse.saddle")
                    {
                        // Copy serialized fields, and change the short name. This will reset the cached ItemDefinition.
                        offer.SellItem = offer.SellItem.Copy();
                        offer.SellItem.ShortName = "horse.saddle.single";
                        singleSaddleOffer = offer;
                        singleSaddleIndex = i;
                        break;
                    }
                }

                if (singleSaddleOffer != null && singleSaddleIndex >= 0 && Offers.Length < MaxVendingOffers)
                {
                    var doubleSaddleOffer = singleSaddleOffer.Copy();
                    doubleSaddleOffer.SellItem.ShortName = "horse.saddle.double";
                    doubleSaddleOffer.CurrencyItem.Amount = Mathf.FloorToInt(doubleSaddleOffer.CurrencyItem.Amount * 1.2f);

                    var newOfferList = new List<VendingOffer>(Offers);
                    newOfferList.Insert(singleSaddleIndex + 1, doubleSaddleOffer);
                    Offers = newOfferList.ToArray();
                }
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private abstract class BaseVendingProfileDataFile
        {
            [JsonProperty("VendingProfiles")]
            public List<VendingProfile> VendingProfiles { get; } = new();

            public abstract void Save();
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class SavedPrefabRelativeData : BaseVendingProfileDataFile
        {
            private static IDataLoader<SavedPrefabRelativeData> _dataLoader = new JsonLoader<SavedPrefabRelativeData>(nameof(CustomVendingSetup));

            public static SavedPrefabRelativeData Load()
            {
                return _dataLoader.Load();
            }

            public override void Save()
            {
                _dataLoader.Save(this);
            }

            public VendingProfile FindProfile<T>(T location) where T : IRelativePosition
            {
                foreach (var profile in VendingProfiles)
                {
                    if (LocationsMatch(profile, location))
                        return profile;
                }

                return null;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class SavedMapData : BaseVendingProfileDataFile
        {
            private static IDataLoader _dataLoader = new JsonLoader();

            // Return example: proceduralmap.1500.548423.212
            private static string GetPerWipeSaveName()
            {
                return World.SaveFileName[..World.SaveFileName.LastIndexOf(".")];
            }

            // Return example: proceduralmap.1500.548423
            private static string GetCrossWipeSaveName()
            {
                var saveName = GetPerWipeSaveName();
                return saveName[..saveName.LastIndexOf(".")];
            }

            private static bool IsProcedural() => World.SaveFileName.StartsWith("proceduralmap");

            private static string GetPerWipeFilePath() => $"{nameof(CustomVendingSetup)}/{GetPerWipeSaveName()}";
            private static string GetCrossWipeFilePath() => $"{nameof(CustomVendingSetup)}/{GetCrossWipeSaveName()}";
            private static string GetFilepath() => IsProcedural() ? GetPerWipeFilePath() : GetCrossWipeFilePath();
            public static string GetMapName() => IsProcedural() ? GetPerWipeSaveName() : GetCrossWipeSaveName();

            public static SavedMapData Load()
            {
                return _dataLoader.Load<SavedMapData>(GetFilepath());
            }

            public override void Save()
            {
                _dataLoader.Save(GetFilepath(), this);
            }

            public VendingProfile FindProfile(Vector3 position)
            {
                foreach (var vendingProfile in VendingProfiles)
                {
                    if (AreVectorsClose(vendingProfile.Position, position))
                        return vendingProfile;
                }

                return null;
            }
        }

        [ProtoContract]
        [JsonObject(MemberSerialization.OptIn)]
        private class CustomSalesData
        {
            public static CustomSalesData FromVendingMachineSalesData(NPCVendingMachine.SalesData salesData)
            {
                return new CustomSalesData
                {
                    CurrentMultiplier = salesData.CurrentMultiplier,
                    SoldThisInterval = salesData.SoldThisInterval,
                    TotalIntervals = salesData.TotalIntervals,
                    TotalSales = salesData.TotalSales,
                };
            }

            [ProtoMember(1)]
            [JsonProperty("CurrentMultiplier")]
            public float CurrentMultiplier;

            [ProtoMember(2)]
            [JsonProperty("SoldThisInterval")]
            public ulong SoldThisInterval;

            [ProtoMember(3)]
            [JsonProperty("TotalIntervals")]
            public ulong TotalIntervals;

            [ProtoMember(4)]
            [JsonProperty("TotalSales")]
            public ulong TotalSales;

            public NPCVendingMachine.SalesData ToVendingMachineSalesData()
            {
                var vendingMachineSalesData = new NPCVendingMachine.SalesData();
                CopyToVendingMachineSalesData(vendingMachineSalesData);
                return vendingMachineSalesData;
            }

            public void CopyToVendingMachineSalesData(NPCVendingMachine.SalesData salesData)
            {
                salesData.CurrentMultiplier = CurrentMultiplier;
                salesData.SoldThisInterval = SoldThisInterval;
                salesData.TotalIntervals = TotalIntervals;
                salesData.TotalSales = TotalSales;
            }
        }

        [ProtoContract]
        public struct SerializableVector3
        {
            [ProtoMember(1)]
            [JsonProperty("x")]
            public readonly float x;

            [ProtoMember(2)]
            [JsonProperty("y")]
            public readonly float y;

            [ProtoMember(3)]
            [JsonProperty("z")]
            public readonly float z;

            public SerializableVector3() {}

            public SerializableVector3(float x, float y, float z)
            {
                this.x = x;
                this.y = y;
                this.z = z;
            }

            public static implicit operator Vector3(SerializableVector3 vector)
            {
                return new Vector3(vector.x, vector.y, vector.z);
            }

            public static implicit operator SerializableVector3(Vector3 vector)
            {
                return new SerializableVector3(vector.x, vector.y, vector.z);
            }
        }

        [ProtoContract]
        [JsonObject(MemberSerialization.OptIn)]
        private class VendingMachineState
        {
            private static readonly FieldInfo AllSalesDataField = typeof(NPCVendingMachine)
                .GetField("allSalesData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            public static VendingMachineState FromVendingMachine(NPCVendingMachine vendingMachine)
            {
                var salesData = (NPCVendingMachine.SalesData[])AllSalesDataField?.GetValue(vendingMachine)
                                ?? Array.Empty<NPCVendingMachine.SalesData>();

                return new VendingMachineState
                {
                    EntityId = vendingMachine.net.ID.Value,
                    SalesData = salesData.Select(CustomSalesData.FromVendingMachineSalesData).ToArray(),
                    Position = vendingMachine.transform.position,
                };
            }

            [ProtoMember(1)]
            [JsonProperty("EntityId")]
            public ulong EntityId;

            [ProtoMember(2)]
            [JsonProperty("SalesData")]
            public CustomSalesData[] SalesData = Array.Empty<CustomSalesData>();

            [ProtoMember(3)]
            [JsonProperty("Position")]
            public SerializableVector3 Position;

            public void ApplyToVendingMachine(NPCVendingMachine vendingMachine)
            {
                var salesData = SalesData?.Select(data => data.ToVendingMachineSalesData())
                        .Take(vendingMachine.sellOrders.sellOrders.Count)
                        .ToArray() ?? Array.Empty<NPCVendingMachine.SalesData>();

                AllSalesDataField?.SetValue(vendingMachine, salesData);
            }
        }

        [ProtoContract]
        [JsonObject(MemberSerialization.OptIn)]
        private class SavedSalesData
        {
            private static string FileName = $"{nameof(CustomVendingSetup)}_SalesData";
            private static IDataLoader<SavedSalesData> DataLoader = new ProtoLoader<SavedSalesData>(FileName);

            public static SavedSalesData Load()
            {
                return DataLoader.Load();
            }

            [ProtoMember(1)]
            [JsonProperty("VendingMachines")]
            public List<VendingMachineState> VendingMachines = new();

            public void Save()
            {
                DataLoader.Save(this);
            }

            public void Reset()
            {
                if (VendingMachines.Count == 0)
                    return;

                VendingMachines.Clear();
                Save();
            }

            public VendingMachineState FindState(NPCVendingMachine vendingMachine)
            {
                var position = vendingMachine.transform.position;

                foreach (var vendingMachineState in VendingMachines)
                {
                    if (vendingMachineState.EntityId == vendingMachine.net.ID.Value
                        || AreVectorsClose(vendingMachineState.Position, position))
                        return vendingMachineState;
                }

                return null;
            }
        }

        #endregion

        #region Configuration

        [JsonObject(MemberSerialization.OptIn)]
        private class ShopUISettings
        {
            [JsonProperty("Enable skin overlays")]
            public bool EnableSkinOverlays = true;
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class PaymentProviderConfig
        {
            [JsonProperty("Enabled")]
            public bool Enabled;

            [JsonProperty("Item short name")]
            public string ItemShortName;

            [JsonProperty("Item skin ID")]
            public ulong ItemSkinId;

            public ItemDefinition ItemDefinition { get; private set; }

            public bool EnabledAndValid => Enabled && (object)ItemDefinition != null;

            public void Init()
            {
                if (string.IsNullOrWhiteSpace(ItemShortName))
                    return;

                ItemDefinition = ItemManager.FindItemDefinition(ItemShortName);
                if (ItemDefinition == null)
                {
                    LogError($"Invalid item short name in config: {ItemShortName}");
                }
            }

            public bool MatchesItem(VendingItem vendingItem)
            {
                return Enabled && vendingItem.ItemDefinition == ItemDefinition && vendingItem.SkinId == ItemSkinId;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class Configuration : SerializableConfiguration
        {
            [JsonProperty("Shop UI settings")]
            public ShopUISettings ShopUISettings = new();

            [JsonProperty("Economics integration")]
            public PaymentProviderConfig Economics = new();

            [JsonProperty("Server Rewards integration")]
            public PaymentProviderConfig ServerRewards = new();

            [JsonProperty("Override item max stack sizes (shortname: amount)")]
            public Dictionary<string, int> ItemStackSizeOverrides = new();

            public void Init()
            {
                Economics.Init();
                ServerRewards.Init();

                foreach (var entry in ItemStackSizeOverrides)
                {
                    if (ItemManager.FindItemDefinition(entry.Key) == null)
                    {
                        LogError($"Invalid item short name in config: {entry.Key}");
                    }
                }
            }

            public int GetItemMaxStackSize(Item item)
            {
                var maxStackSize = item.MaxStackable();

                if (ItemStackSizeOverrides.TryGetValue(item.info.shortname, out var overrideMaxStackSize))
                {
                    maxStackSize = Math.Max(maxStackSize, overrideMaxStackSize);
                }

                return Math.Max(1, maxStackSize);
            }
        }

        private Configuration GetDefaultConfig() => new();

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
            var changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                if (currentRaw.TryGetValue(key, out var currentRawValue))
                {
                    var currentDictValue = currentRawValue as Dictionary<string, object>;
                    if (currentWithDefaults[key] is Dictionary<string, object> defaultDictValue)
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

        protected override void LoadDefaultConfig() => _config = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(_config))
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
            Config.WriteObject(_config, true);
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
            public const string SettingsSkinId = "Settings.SkinId";
            public const string SettingsBypassDynamicPricing = "Settings.BypassDynamicPricing";
            public const string SettingsShopName = "Settings.ShopName";
            public const string ErrorCurrentlyBeingEdited = "Error.CurrentlyBeingEdited";
            public const string InfoDataProviderMap = "Info.DataProvider.Map";
            public const string InfoDataProviderEntity = "Info.DataProvider.Entity";
            public const string InfoDataProviderMonument = "Info.DataProvider.Monument";
            public const string InfoDataProviderPlugin = "Info.DataProvider.Plugin";
            public const string InfoDataProviderPluginUnknownName = "Info.DataProvider.Plugin.UnknownName";
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
                [Lang.SettingsSkinId] = "Skin ID",
                [Lang.SettingsBypassDynamicPricing] = "Bypass Dynamic Pricing",
                [Lang.SettingsShopName] = "Shop Name",
                [Lang.ErrorCurrentlyBeingEdited] = "That vending machine is currently being edited by {0}.",
                [Lang.InfoDataProviderMap] = "Data Provider: <color=#f90>Map ({0})</color>",
                [Lang.InfoDataProviderEntity] = "Data Provider: <color=#6f6>Entity ({0})</color>",
                [Lang.InfoDataProviderMonument] = "Data Provider: <color=#6f6>Monument ({0})</color>",
                [Lang.InfoDataProviderPlugin] = "Data Provider: <color=#f9f>Plugin ({0})</color>",
                [Lang.InfoDataProviderPluginUnknownName] = "Data Provider: <color=#f9f>Plugin</color>",
            }, this, "en");
        }

        #endregion
    }
}
