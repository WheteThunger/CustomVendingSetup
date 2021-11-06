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
    [Info("Custom Vending Setup", "WhiteThunder", "2.1.0")]
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

        // Going over 7 causes offers to get cut off regardless of resolution.
        private const int MaxVendingOffers = 7;

        private const int NoteSlot = 5;
        private const int ContainerCapacity = 30;
        private const int MaxItemRows = ContainerCapacity / ItemsPerRow;

        private VendingMachineManager _vendingMachineManager = new VendingMachineManager();
        private VendingUIManager _vendingUIManager = new VendingUIManager();
        private ContainerUIManager _containerUIManager = new ContainerUIManager();

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
                offers[i] = VendingOffer.FromVanillaSellOrder(vanillaOffers[i]);

            return offers;
        }

        private static VendingOffer[] GetOffersFromContainer(ItemContainer container)
        {
            var offers = new List<VendingOffer>();

            for (var columnIndex = 0; columnIndex < 2; columnIndex++)
            {
                for (var rowIndex = 0; rowIndex < MaxItemRows; rowIndex++)
                {
                    var sellItemSlot = rowIndex * ItemsPerRow + columnIndex * 2;

                    var sellItem = container.GetSlot(sellItemSlot);
                    var currencyItem = container.GetSlot(sellItemSlot + 1);
                    if (sellItem == null || currencyItem == null)
                        continue;

                    offers.Add(new VendingOffer
                    {
                        SellItem = VendingItem.FromItem(sellItem),
                        CurrencyItem = VendingItem.FromItem(currencyItem),
                    });
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

            return (orderIndex % MaxItemRows) * ItemsPerRow + 2;
        }

        private static StorageContainer CreateOrdersContainer(VendingOffer[] vendingOffers, string shopName)
        {
            var containerEntity = CreateContainerEntity(StoragePrefab);

            var container = containerEntity.inventory;
            container.allowedContents = ItemContainer.ContentsType.Generic;
            container.capacity = ContainerCapacity;

            for (var orderIndex = 0; orderIndex < vendingOffers.Length && orderIndex < 2 * MaxItemRows; orderIndex++)
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

            var noteItem = ItemManager.CreateByItemID(NoteItemId);
            if (noteItem != null)
            {
                noteItem.text = shopName;
                if (!noteItem.MoveToContainer(container, NoteSlot))
                    noteItem.Remove();
            }

            return containerEntity;
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

                Profile.Offers = GetOffersFromContainer(Container.inventory);
                Profile.Broadcast = EditFormState.Broadcast;

                var updatedShopName = Container.inventory.GetSlot(NoteSlot)?.text.Trim();
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

                Container = CreateOrdersContainer(offers, vendingMachine.shopName);
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

                for (var i = 0; i < Profile.Offers.Length && i < MaxVendingOffers; i++)
                {
                    var offer = Profile.Offers[i];
                    if (!offer.IsValid)
                        continue;

                    baseEntity.AddSellOrder(
                        offer.SellItem.ItemId,
                        offer.SellItem.Amount,
                        offer.CurrencyItem.ItemId,
                        offer.CurrencyItem.Amount,
                        baseEntity.GetBPState(offer.SellItem.IsBlueprint, offer.CurrencyItem.IsBlueprint)
                    );
                }

                CustomRefill(maxRefill: true);
            }

            private void CustomRefill(bool maxRefill = false)
            {
                for (var i = 0; i < Profile.Offers.Length; i++)
                {
                    if (_refillTimes[i] > Time.realtimeSinceStartup)
                        continue;

                    var offer = Profile.Offers[i];
                    if (!offer.IsValid)
                        continue;

                    var totalAmountOfItem = offer.SellItem.GetAmountInContainer(baseEntity.inventory);
                    var numPurchasesPossible = totalAmountOfItem / offer.SellItem.Amount;
                    var refillNumberOfPurchases = offer.RefillMax - numPurchasesPossible;

                    if (!maxRefill)
                        refillNumberOfPurchases = Mathf.Min(refillNumberOfPurchases, offer.RefillAmount);

                    var refillAmount = refillNumberOfPurchases * offer.SellItem.Amount;
                    if (refillAmount > 0)
                    {
                        baseEntity.transactionActive = true;
                        var item = offer.SellItem.Create(refillAmount);
                        if (item != null)
                        {
                            if (!item.MoveToContainer(baseEntity.inventory))
                                item.Remove();
                        }
                        baseEntity.transactionActive = false;
                    }

                    _refillTimes[i] = Time.realtimeSinceStartup + offer.RefillDelay;
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

            public int GetAmountInContainer(ItemContainer container)
            {
                var count = 0;
                foreach (var item in container.itemList)
                {
                    var itemMatches = IsBlueprint
                        ? item.info == _pluginInstance?._blueprintDefinition && item.blueprintTarget == ItemId
                        : item.info.itemid == ItemId;

                    if (itemMatches)
                        count += item.amount;
                }
                return count;
            }
        }

        private class VendingOffer
        {
            public static VendingOffer FromVanillaSellOrder(SellOrder sellOrder)
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
                };
            }

            [JsonProperty("SellItem")]
            public VendingItem SellItem;

            [JsonProperty("CurrencyItem")]
            public VendingItem CurrencyItem;

            [JsonProperty("RefillAmount", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(1)]
            public int RefillAmount = 1;

            [JsonProperty("RefillDelay", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(10f)]
            public float RefillDelay = 10;

            [JsonProperty("RefillMax", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [DefaultValue(10)]
            public int RefillMax = 10;

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
