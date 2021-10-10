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
    [Info("Custom Vending Setup", "WhiteThunder", "2.0.0")]
    [Description("Allows editing orders at NPC vending machines.")]
    internal class CustomVendingSetup : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private Plugin MonumentFinder, UIScaleManager;

        private static CustomVendingSetup _pluginInstance;
        private static SavedData _pluginData;

        private const string PermissionUse = "customvendingsetup.use";

        private const string StoragePrefab = "assets/prefabs/deployable/large wood storage/box.wooden.large.prefab";

        private const int NoteItemId = 1414245162;
        private const int ItemsPerRow = 6;

        // Going over 7 causes orders to get cut off regardless of resolution.
        private const int MaxOrderEntries = 7;

        private const int NoteSlot = 5;
        private const int ContainerCapacity = 30;
        private const int MaxItemRows = ContainerCapacity / ItemsPerRow;

        private VendingMachineManager _vendingMachineManager = new VendingMachineManager();
        private VendingUIManager _vendingUIManager = new VendingUIManager();
        private ContainerUIManager _containerUIManager = new ContainerUIManager();

        private StorageContainer _sharedContainerEntity;

        private DynamicHookSubscriber<ulong> _uiViewers = new DynamicHookSubscriber<ulong>(
            nameof(OnLootEntityEnd)
        );

        private ItemDefinition _blueprintDefinition;
        private bool _serverInitialized = false;

        #endregion

        #region Hooks

        private void Init()
        {
            _pluginInstance = this;
            _pluginData = SavedData.Load();

            permission.RegisterPermission(PermissionUse, this);

            _uiViewers.UnsubscribeAll();
        }

        private void OnServerInitialized()
        {
            if (CheckDependencies())
                NextTick(_vendingMachineManager.SetupAll);

            _sharedContainerEntity = CreateContainerEntity(StoragePrefab);
            if (_sharedContainerEntity == null)
                LogError($"Failed to create storage entity with prefab: {StoragePrefab}");

            foreach (var player in BasePlayer.activePlayerList)
            {
                var container = player.inventory.loot.containers.FirstOrDefault();
                if (container == null)
                    continue;

                var vendingMachine = container.entityOwner as NPCVendingMachine;
                if (vendingMachine != null)
                    OnLootEntity(player, vendingMachine);
            }

            _blueprintDefinition = ItemManager.FindItemDefinition("blueprintbase");
            _serverInitialized = true;
        }

        private void Unload()
        {
            _vendingUIManager.DestroyForAllPlayers();
            _containerUIManager.DestroyForAllPlayers();

            _vendingMachineManager.ResetAll();

            if (_sharedContainerEntity != null)
                _sharedContainerEntity.Kill();

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
            _vendingMachineManager.OnVendingMachineSpawned(vendingMachine);
        }

        private void OnEntityKill(NPCVendingMachine vendingMachine)
        {
            _vendingMachineManager.OnVendingMachineKilled(vendingMachine);
        }

        private void OnLootEntity(BasePlayer player, NPCVendingMachine vendingMachine)
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

            if (storageContainer == _sharedContainerEntity)
            {
                var container = player.inventory.loot.containers.FirstOrDefault();
                var controller = _vendingMachineManager.GetControllerByContainer(container);
                if (controller == null)
                    return;

                _containerUIManager.DestroyForPlayer(player);
                _uiViewers.Remove(player.userID);
                controller.OnContainerClosed();

                return;
            }
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
            public string Alias => (string)_monumentInfo["Alias"];

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

        private float GetPlayerUIScale(BasePlayer player)
        {
            var scaleResult = UIScaleManager?.Call("API_CheckPlayerUIInfo", player.UserIDString) as float[];
            return scaleResult != null && scaleResult.Length >= 3
                ? scaleResult[2]
                : 1;
        }

        #endregion

        #region Exposed Hooks

        private bool SetupVendingMachineWasBlocked(NPCVendingMachine vendingMachine)
        {
            object hookResult = Interface.CallHook("OnCustomVendingSetup", vendingMachine);
            return hookResult is bool && (bool)hookResult == false;
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
            NPCVendingMachine vendingMachine;
            VendingController controller;
            if (!PassesUICommandChecks(player, args, out vendingMachine, out controller))
                return;

            var basePlayer = player.Object as BasePlayer;

            if (args.Length < 2)
                return;

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

        private static void StartLooting(BasePlayer player, ItemContainer container, string panelName = null)
        {
            var containerEntity = _pluginInstance._sharedContainerEntity;

            player.inventory.loot.Clear();
            player.inventory.loot.PositionChecks = false;
            player.inventory.loot.entitySource = containerEntity;
            player.inventory.loot.itemSource = null;
            player.inventory.loot.MarkDirty();
            player.inventory.loot.AddContainer(container);
            player.inventory.loot.SendImmediate();

            player.ClientRPCPlayer(null, player, "RPC_OpenLootPanel", panelName ?? containerEntity.panelName);
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

        private static CustomOrderEntry[] GetEntriesFromVendingMachine(NPCVendingMachine vendingMachine)
        {
            var vanillaEntries = vendingMachine.vendingOrders.orders;
            var entries = new CustomOrderEntry[vanillaEntries.Length];

            for (var i = 0; i < vanillaEntries.Length; i++)
                entries[i] = CustomOrderEntry.FromVanillaOrderEntry(vanillaEntries[i]);

            return entries;
        }

        private static CustomOrderEntry[] GetEntriesFromContainer(ItemContainer container)
        {
            var entries = new List<CustomOrderEntry>();

            for (var columnIndex = 0; columnIndex < 2; columnIndex++)
            {
                for (var rowIndex = 0; rowIndex < MaxItemRows; rowIndex++)
                {
                    var sellItemSlot = rowIndex * ItemsPerRow + columnIndex * 2;

                    var sellItem = container.GetSlot(sellItemSlot);
                    var currencyItem = container.GetSlot(sellItemSlot + 1);
                    if (sellItem == null || currencyItem == null)
                        continue;

                    entries.Add(new CustomOrderEntry
                    {
                        SellItem = ItemInfo.FromItem(sellItem),
                        CurrencyItem = ItemInfo.FromItem(currencyItem),
                    });
                }
            }

            return entries.ToArray();
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

        private static ItemContainer CreateContainer()
        {
            var container = new ItemContainer();
            container.entityOwner = _pluginInstance._sharedContainerEntity;
            container.isServer = true;
            container.GiveUID();
            return container;
        }

        private static int OrderIndexToSlot(int orderIndex)
        {
            if (orderIndex < MaxItemRows)
                return orderIndex * ItemsPerRow;

            return (orderIndex % MaxItemRows) * ItemsPerRow + 2;
        }

        private static ItemContainer CreateOrdersContainer(CustomOrderEntry[] customOrderEntries, string shopName)
        {
            var container = CreateContainer();
            container.allowedContents = ItemContainer.ContentsType.Generic;
            container.capacity = ContainerCapacity;

            for (var orderIndex = 0; orderIndex < customOrderEntries.Length && orderIndex < 2 * MaxItemRows; orderIndex++)
            {
                var entry = customOrderEntries[orderIndex];
                var sellItem = entry.SellItem.Create();
                var currencyItem = entry.CurrencyItem.Create();

                if (sellItem == null || currencyItem == null)
                    continue;

                var destinationSlot = OrderIndexToSlot(orderIndex);

                if (!sellItem.MoveToContainer(container, destinationSlot))
                    sellItem.Remove();

                if (!currencyItem.MoveToContainer(container, destinationSlot + 1))
                    currencyItem.Remove();
            }

            var noteItem = ItemManager.CreateByItemID(NoteItemId);
            if (noteItem != null)
            {
                noteItem.text = shopName;
                if (!noteItem.MoveToContainer(container, NoteSlot))
                    noteItem.Remove();
            }

            return container;
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

            public virtual void CreateUI(BasePlayer player, string json)
            {
                _viewingPlayers.Add(player);
                CuiHelper.AddUi(player, json);
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

                var uiScale = _pluginInstance.GetPlayerUIScale(player);

                var numSellOrders = vendingMachine.sellOrders?.sellOrders.Count ?? 0;
                var offsetY = (136 + 74 * numSellOrders) * uiScale;
                var offsetX = 192 * uiScale;

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
                    AddVendingButton(cuiElements, uiScale, vendingMachineId, resetButtonText, UICommands.Reset, buttonIndex, UIConstants.ResetButtonColor, UIConstants.ResetButtonTextColor);
                    buttonIndex++;
                }

                var editButtonText = _pluginInstance.GetMessage(player, Lang.ButtonEdit);
                AddVendingButton(cuiElements, uiScale, vendingMachineId, editButtonText, UICommands.Edit, buttonIndex, UIConstants.SaveButtonColor, UIConstants.SaveButtonTextColor);

                CreateUI(player, cuiElements);
            }

            private float GetButtonOffset(int reverseButtonIndex)
            {
                return UIConstants.PanelWidth - reverseButtonIndex * (UIConstants.ButtonWidth + UIConstants.ButtonHorizontalSpacing);
            }

            private void AddVendingButton(CuiElementContainer cuiElements, float uiScale, uint vendingMachineId, string text, string subCommand, int reverseButtonIndex, string color, string textColor)
            {
                var xMax = GetButtonOffset(reverseButtonIndex) * uiScale;
                var xMin = xMax - UIConstants.ButtonWidth * uiScale;

                cuiElements.Add(
                    new CuiButton
                    {
                        Text =
                        {
                            Text = text,
                            Color = textColor,
                            Align = TextAnchor.MiddleCenter,
                            FontSize = Convert.ToInt32(18 * uiScale),
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
                            OffsetMax = $"{xMax} {UIConstants.ButtonHeight * uiScale}",
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

                var uiScale = _pluginInstance.GetPlayerUIScale(player);

                var offsetX = 192 * uiScale;
                var offsetY = 139 * uiScale;

                var cuiElements = new CuiElementContainer
                {
                    {
                        new CuiPanel
                        {
                            RectTransform =
                            {
                                AnchorMin = UIConstants.AnchorMin,
                                AnchorMax = UIConstants.AnchorMax,
                                OffsetMin = $"{offsetX} {offsetY + MaxItemRows * (UIConstants.ItemBoxSize + UIConstants.ItemSpacing) * uiScale}",
                                OffsetMax = $"{offsetX} {offsetY + MaxItemRows * (UIConstants.ItemBoxSize + UIConstants.ItemSpacing) * uiScale}",
                            },
                        },
                        "Overlay",
                        UIName
                    }
                };

                var saveButtonText = _pluginInstance.GetMessage(player, Lang.ButtonSave);
                var cancelButtonText = _pluginInstance.GetMessage(player, Lang.ButtonCancel);

                var vendingMachineId = vendingMachine.net.ID;

                AddButton(cuiElements, uiScale, vendingMachineId, saveButtonText, UICommands.Save, 1, UIConstants.SaveButtonColor, UIConstants.SaveButtonTextColor);
                AddButton(cuiElements, uiScale, vendingMachineId, cancelButtonText, UICommands.Cancel, 0, UIConstants.CancelButtonColor, UIConstants.CancelButtonTextColor);
                AddBroadcastButton(cuiElements, uiScale, vendingMachine, uiState);

                var headerOffset = -6 * uiScale;

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
                                OffsetMin = $"0 {headerOffset - UIConstants.HeaderHeight * uiScale}",
                                OffsetMax = $"{UIConstants.PanelWidth * uiScale} {headerOffset}",
                            }
                        },
                        Parent = UIName,
                        Name = TipUIName,
                    }
                );

                var forSaleText = _pluginInstance.GetMessage(player, Lang.InfoForSale);
                var costText = _pluginInstance.GetMessage(player, Lang.InfoCost);
                var shopNameText = _pluginInstance.GetMessage(player, Lang.InfoShopName);

                AddHeaderLabel(cuiElements, uiScale, 0, forSaleText);
                AddHeaderLabel(cuiElements, uiScale, 1, costText);
                AddHeaderLabel(cuiElements, uiScale, 2, forSaleText);
                AddHeaderLabel(cuiElements, uiScale, 3, costText);
                AddHeaderLabel(cuiElements, uiScale, 5, shopNameText);

                CreateUI(player, cuiElements);
            }

            private void AddHeaderLabel(CuiElementContainer cuiElements, float uiScale, int index, string text)
            {
                float xMin = (6 + index * (UIConstants.ItemBoxSize + UIConstants.ItemSpacing)) * uiScale;
                float xMax = xMin + UIConstants.ItemBoxSize * uiScale;

                cuiElements.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = text,
                            Color = UIConstants.CancelButtonTextColor,
                            Align = TextAnchor.MiddleCenter,
                            FontSize = Convert.ToInt32(13 * uiScale),
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "0 0",
                            OffsetMin = $"{xMin} 0",
                            OffsetMax = $"{xMax} {UIConstants.HeaderHeight * uiScale}",
                        }
                    },
                    TipUIName
                );
            }

            private void AddBroadcastButton(CuiElementContainer cuiElements, float uiScale, NPCVendingMachine vendingMachine, EditFormState uiState)
            {
                var iconSize = UIConstants.ButtonHeight * uiScale;

                var xMax = GetButtonOffset(2) * uiScale;
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
                                OffsetMax = $"{xMax} {UIConstants.ButtonHeight * uiScale}",
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

                var uiScale = _pluginInstance.GetPlayerUIScale(player);
                var cuiElements = new CuiElementContainer();
                AddBroadcastButton(cuiElements, uiScale, vendingMachine, uiState);
                CuiHelper.AddUi(player, cuiElements);
            }

            private float GetButtonOffset(int buttonIndex)
            {
                return UIConstants.PanelWidth - buttonIndex * (UIConstants.ButtonWidth + UIConstants.ButtonHorizontalSpacing);
            }

            private void AddButton(CuiElementContainer cuiElements, float uiScale, uint vendingMachineId, string text, string subCommand, int buttonIndex, string color, string textColor)
            {
                var xMax = GetButtonOffset(buttonIndex) * uiScale;
                var xMin = xMax - UIConstants.ButtonWidth * uiScale;

                cuiElements.Add(
                    new CuiButton
                    {
                        Text =
                        {
                            Text = text,
                            Color = textColor,
                            Align = TextAnchor.MiddleCenter,
                            FontSize = Convert.ToInt32(18 * uiScale),
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
                            OffsetMax = $"{xMax} {UIConstants.ButtonHeight * uiScale}",
                        },
                    },
                    UIName
                );
            }
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

                if (_pluginInstance.SetupVendingMachineWasBlocked(vendingMachine))
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

            public VendingController GetControllerByContainer(ItemContainer container)
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
                    if (controller.Location.Matches(location))
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
            public ItemContainer Container { get; private set; }
            public BasePlayer EditorPlayer { get; private set; }
            public EditFormState EditFormState { get; private set; }

            // List of vending machines with a position matching this controller.
            private HashSet<NPCVendingMachine> _vendingMachineList = new HashSet<NPCVendingMachine>();

            public VendingController(MonumentRelativePosition location)
            {
                Location = location;
                Profile = _pluginData.FindProfile(location);
            }

            public void SetupAll()
            {
                foreach (var vendingMachine in _vendingMachineList)
                    VendingMachineComponent.AddToVendingMachine(vendingMachine, Profile);
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

                Profile.OrderEntries = GetEntriesFromContainer(Container);
                Profile.Broadcast = EditFormState.Broadcast;

                var updatedShopName = Container.GetSlot(NoteSlot)?.text.Trim();
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

                var orderEntries = Profile?.OrderEntries ?? GetEntriesFromVendingMachine(vendingMachine);

                Container = CreateOrdersContainer(orderEntries, vendingMachine.shopName);
                EditFormState = EditFormState.FromVendingMachine(vendingMachine);
                StartLooting(player, Container);
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

            private void KillContainer()
            {
                Container?.Kill();
                Container = null;
            }
        }

        #endregion

        #region Vending Machine Component

        private class VendingMachineComponent : EntityComponent<NPCVendingMachine>
        {
            private const int MaxPurchasesToStock = 10;

            public static void AddToVendingMachine(NPCVendingMachine vendingMachine, VendingProfile profile) =>
                vendingMachine.GetOrAddComponent<VendingMachineComponent>().AssignProfile(profile);

            public static void RemoveFromVendingMachine(NPCVendingMachine vendingMachine) =>
                DestroyImmediate(vendingMachine.GetComponent<VendingMachineComponent>());

            private VendingProfile _profile;
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
                _profile = profile;
                _refillTimes = new float[_profile.OrderEntries.Length];

                baseEntity.inventory.Clear();
                ItemManager.DoRemoves();
                baseEntity.ClearSellOrders();

                if (_originalShopName == null)
                    _originalShopName = baseEntity.shopName;

                if (_originalBroadcast == null)
                    _originalBroadcast = baseEntity.IsBroadcasting();

                if (!string.IsNullOrEmpty(profile.ShopName))
                {
                    baseEntity.shopName = profile.ShopName;
                }

                if (baseEntity.IsBroadcasting() != profile.Broadcast)
                {
                    baseEntity.SetFlag(VendingMachineFlags.Broadcasting, profile.Broadcast);
                    baseEntity.UpdateMapMarker();
                }

                for (var i = 0; i < _profile.OrderEntries.Length && i < MaxOrderEntries; i++)
                {
                    var entry = _profile.OrderEntries[i];
                    if (!entry.IsValid)
                        continue;

                    baseEntity.AddSellOrder(
                        entry.SellItem.ItemId,
                        entry.SellItem.Amount,
                        entry.CurrencyItem.ItemId,
                        entry.CurrencyItem.Amount,
                        baseEntity.GetBPState(entry.SellItem.IsBlueprint, entry.CurrencyItem.IsBlueprint)
                    );
                }

                CustomRefill(maxRefill: true);
            }

            private void CustomRefill(bool maxRefill = false)
            {
                for (var i = 0; i < _profile.OrderEntries.Length; i++)
                {
                    if (_refillTimes[i] > Time.realtimeSinceStartup)
                        continue;

                    var entry = _profile.OrderEntries[i];
                    if (!entry.IsValid)
                        continue;

                    var totalAmountOfItem = entry.SellItem.GetAmountInContainer(baseEntity.inventory);
                    var numPurchasesPossible = totalAmountOfItem / entry.SellItem.Amount;
                    var refillNumberOfPurchases = MaxPurchasesToStock - numPurchasesPossible;

                    if (!maxRefill)
                        refillNumberOfPurchases = Mathf.Min(refillNumberOfPurchases, entry.RefillAmount);

                    var refillAmount = refillNumberOfPurchases * entry.SellItem.Amount;
                    if (refillAmount > 0)
                    {
                        baseEntity.transactionActive = true;
                        var item = entry.SellItem.Create(refillAmount);
                        if (item != null)
                        {
                            if (!item.MoveToContainer(baseEntity.inventory))
                                item.Remove();
                        }
                        baseEntity.transactionActive = false;
                    }

                    _refillTimes[i] = Time.realtimeSinceStartup + entry.RefillDelay;
                }
            }

            private void TimedRefill()
            {
                _pluginInstance?.TrackStart();
                CustomRefill();
                _pluginInstance?.TrackEnd();
            }

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

        #region Saved Data

        private class ItemInfo
        {
            public static ItemInfo FromItem(Item item)
            {
                var isBlueprint = item.IsBlueprint();
                var itemDefinition = isBlueprint
                    ? ItemManager.FindItemDefinition(item.blueprintTarget)
                    : item.info;

                return new ItemInfo
                {
                    ShortName = itemDefinition.shortname,
                    Amount = item.amount,
                    Name = item.name,
                    Skin = item.skin,
                    IsBlueprint = isBlueprint,
                };
            }

            [JsonProperty("ShortName")]
            public string ShortName;

            [JsonProperty("Amount")]
            public int Amount = 0;

            [JsonProperty("Name", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Name;

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
                IsValid ? CreateItem(Definition, amount, Name, Skin, IsBlueprint) : null;

            public Item Create() => Create(Amount);

            public int GetAmountInContainer(ItemContainer container)
            {
                var count = 0;
                foreach (var item in container.itemList)
                {
                    var itemMatches = IsBlueprint
                        ? item.info == _pluginInstance?._blueprintDefinition && item.blueprintTarget == ItemId
                        : item.info.itemid == ItemId;

                    if (itemMatches)
                        count++;
                }
                return count;
            }
        }

        private class CustomOrderEntry
        {
            public static CustomOrderEntry FromVanillaOrderEntry(NPCVendingOrder.Entry entry)
            {
                return new CustomOrderEntry
                {
                    SellItem = new ItemInfo
                    {
                        ShortName = entry.sellItem.shortname,
                        Amount = entry.sellItemAmount,
                        IsBlueprint = entry.sellItemAsBP,
                    },
                    CurrencyItem = new ItemInfo
                    {
                        ShortName = entry.currencyItem.shortname,
                        Amount = entry.currencyAmount,
                        IsBlueprint = entry.currencyAsBP,
                    },
                };
            }

            [JsonProperty("SellItem")]
            public ItemInfo SellItem;

            [JsonProperty("CurrencyItem")]
            public ItemInfo CurrencyItem;

            [JsonProperty("RefillAmount", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(1)]
            public int RefillAmount = 1;

            [JsonProperty("RefillDelay", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(10f)]
            public float RefillDelay = 10;

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

        private static bool AreVectorsClose(Vector3 a, Vector3 b, float xZTolerance = 0.001f, float yTolerance = 10)
        {
            var diff = a - b;

            // Allow a generous amount of vertical distance given that plugins may snap entities to terrain.
            return Math.Abs(diff.y) < yTolerance
                && Math.Abs(diff.x) < xZTolerance
                && Math.Abs(diff.z) < xZTolerance;
        }

        private struct MonumentRelativePosition
        {
            public static MonumentRelativePosition? FromVendingMachine(NPCVendingMachine vendingMachine)
            {
                var monument = _pluginInstance.GetMonumentAdapter(vendingMachine);
                if (monument == null)
                    return null;

                return new MonumentRelativePosition
                {
                    Monument = monument.Alias,
                    Position = monument.InverseTransformPoint(vendingMachine.transform.position),
                };
            }

            public string Monument;
            public Vector3 Position;

            public bool Matches(MonumentRelativePosition other)
            {
                return Monument == other.Monument
                    && AreVectorsClose(Position, other.Position);
            }
        }

        private class VendingProfile
        {
            public static VendingProfile FromVendingMachine(MonumentRelativePosition location, NPCVendingMachine vendingMachine)
            {
                return new VendingProfile
                {
                    Monument = location.Monument,
                    Position = location.Position,
                    ShopName = vendingMachine.shopName,
                    Broadcast = vendingMachine.IsBroadcasting(),
                    OrderEntries = GetEntriesFromVendingMachine(vendingMachine),
                };
            }

            [JsonProperty("ShopName", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string ShopName;

            [JsonProperty("Broadcast", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(true)]
            public bool Broadcast = true;

            [JsonProperty("Monument")]
            public string Monument;

            [JsonProperty("Position")]
            public Vector3 Position;

            [JsonProperty("OrderEntries")]
            public CustomOrderEntry[] OrderEntries;

            public bool MatchesLocation(MonumentRelativePosition location)
            {
                return Monument == location.Monument
                    && AreVectorsClose(Position, location.Position);
            }
        }

        private class SavedData
        {
            [JsonProperty("VendingProfiles")]
            public List<VendingProfile> VendingProfiles = new List<VendingProfile>();

            public static SavedData Load() =>
                Interface.Oxide.DataFileSystem.ReadObject<SavedData>(_pluginInstance.Name) ?? new SavedData();

            public void Save() =>
                Interface.Oxide.DataFileSystem.WriteObject<SavedData>(_pluginInstance.Name, this);

            public VendingProfile FindProfile(MonumentRelativePosition location)
            {
                foreach (var profile in VendingProfiles)
                {
                    if (profile.MatchesLocation(location))
                        return profile;
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
            public const string InfoShopName = "Info.Name";
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
                [Lang.InfoShopName] = "NAME",
                [Lang.ErrorCurrentlyBeingEdited] = "That vending machine is currently being edited by {0}.",
            }, this, "en");
        }

        #endregion
    }
}
