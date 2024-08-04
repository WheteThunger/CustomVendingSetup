## Video tutorial

[![Video Tutorial](https://img.youtube.com/vi/oiKKByGV_i0/maxresdefault.jpg)](https://www.youtube.com/watch?v=oiKKByGV_i0)

## Features

- Allows customizing items sold at monument vending machines
- Supports vendors that have invisible vending machines, such as those at bandit camp and fishing villages
- Supports the travelling vendor
- Supports all monument types, including custom monuments
- Saves customizations for future restarts and wipes, based on the vending machine's relative position to the monument
- Synchronizes edits with copies of the vending machines at duplicate monuments
- Compatible with NPC vending machines spawned at monuments by plugins such as [Monument Addons](https://umod.org/plugins/monument-addons)
- Allows bypassing dynamic pricing per vending machine
- Allows customizing vending machine skins
- Allows disabling vending machine map markers
- Allows disabling delivery drone access
- Supports Economics and Server Rewards as both payment and merchandise
- Supports blueprints, as well as items with custom skins and names
- Supports attachments and other child items
- Supports alternative ammo types and amounts

Note: Does not affect player vending machines.

## Optional plugins

- [Monument Finder](https://umod.org/plugins/monument-finder)
  - Installing Monument Finder allows you to save vending machine customizations relative to monuments, automatically applying those customizations on different maps, as long as the vending machines don't move relative to the monuments (Facepunch might move them every few years when re-working a monument).
  - No configuration needed, except for custom monuments.
  - Without Monument Finder, vending machine customizations will apply to only the current map.

## How it works

When you open an NPC vending machine, if you have permission, you will see an edit button. Clicking that edit button will reveal a container UI where you can customize the vending machine.

- Change which items are sold, and their prices, by adding and removing items from the container
- Change display order by rearranging items in the container
- Change stock settings by editing the note next to each item
- Toggle whether the map marker is enabled by clicking on the broadcast icon (green = on, gray = off)
- Toggle whether delivery drones can access the vending machine by clicking on the drone icon (green = on, gray = off)
- Toggle whether the vending machine bypasses dynamic pricing by clicking on the bottom-right note and setting `Bypass Dynamic Pricing: True` or `Bypass Dynamic Pricing: False` (not applicable if the convar` npcvendingmachine.dynamicpricingenabled` is set to `false`)
- Change the vending machine skin and shop name by clicking on the bottom-right note and editing its contents
- Save the changes by clicking the "SAVE" button

### Limitations regarding the map and drone marketplaces

- Skin overlays are not visible while viewing vending machines on the map
- Economics and Server Rewards currency cannot be used to purchase items via drone marketplaces
- When selling items for Economics or Server Rewards currency via a drone marketplace, the player will receive the currency immediately (the drone will travel but not transport any items)

### Limitations regarding the travelling vendor

- In vanilla Rust, the traveling vendor has slightly random items and prices every time it spawns, but this plugin does not currently offer such capabilities, so if you edit the travelling vendor, it will sell the same items for the same prices every time, even if you have multiple travelling vendors
- The map marker and drone accessibility cannot be enabled

### Data providers

When editing a vending machine, you will see some debug text that says "Data Provider: ..." which informs you of how your customizations will be saved and retrieved.
- "Map" -- Indicates that the data is associated with this specific map and will not apply to other maps.
  - The data will be saved at path `oxide/data/CustomVendingSetup/MAP_NAME.json`.
- "Entity" -- Indicates that the data is associated with the vending machine's parent entity. As of this writing, this applies to the travelling vendor.
  - The data will be saved at path `oxide/data/CustomVendingSetup.json`.
- "Monument" -- Indicates that the data is associated with the nearest monument, as determined by Monument Finder.
  - The data will be saved at path `oxide/data/CustomVendingSetup.json`.
- "Plugin" -- Indicates that another plugin is hooking into Custom Vending Setup to handle saving and retrieving data for that specific vending machine. Typically, this is done by plugins that spawn vending machines.
  - The data could be saved anywhere, as it's decided by the plugin that is acting as the data provider.
    - For Monument Addons, the data will be saved inside the profile that spawned the vending machine at `oxide/data/MonumentAddons/PROFILE_NAME.json`
    - For Talking Npc Vendors, the data will be saved inside a vending machine profile at `oxide/data/TalkingNpc/VendingMachines/PROFILE_NAME.json`

If you see "Map" while at a monument, then either you don't have Monument Finder installed, or you need to increase the bounds of the monument via the Monument Finder config to envelop the vending machine's position. Configuring monument bounds is important for custom monuments since there's no reliable way for a plugin to automatically know how large a custom monument is.

## Permissions

- `customvendingsetup.use` -- Allows editing NPC vending machines at monuments.

## Screenshots

![](https://raw.githubusercontent.com/WheteThunger/CustomVendingSetup/master/Images/ShopView.png)

![](https://raw.githubusercontent.com/WheteThunger/CustomVendingSetup/master/Images/ContainerViewStock.png)

![](https://raw.githubusercontent.com/WheteThunger/CustomVendingSetup/master/Images/ContainerViewSettings.png)

## Configuration

Default configuration:

```json
{
  "Shop UI settings": {
    "Enable skin overlays": true
  },
  "Economics integration": {
    "Enabled": false,
    "Item short name": "paper",
    "Item skin ID": 2420097877
  },
  "Server Rewards integration": {
    "Enabled": false,
    "Item short name": "paper",
    "Item skin ID": 2420097877
  },
  "Override item max stack sizes (shortname: amount)": {}
}
```

- `Shop UI settings`
  - `Enable skin overlays` (`true` or `false`) -- While `true`, skin images will be overlaid on top of items when needed. For example, to display currency skin.
- `Economics integration` -- Controls integration with the Economics plugin.
  - `Enabled` (`true` or `false`) -- Determines whether Economics integration is enabled. While enabled, the below configured item will be used as a proxy to configure vending machines to buy and sell Economics currency.
  - `Item short name` -- Determines the item that will be associated with Economics currency. When you want to configure a sale offer to buy or sell Economics currency, you must place this item into the corresponding "For Sale" or "Currency" column while editing the vending machine. Whichever item you configure here will be displayed in the shop view, though you may cover it up with the image of a skin by setting a non-`0` `Item skin ID` and setting `Enable skin overlays` to `true`.
  - `Item skin ID` -- Determines the skin ID that will be associated with Economics currency. If you set this to `0`, the vanilla item (with no skin) will be displayed in the shop view. If you set this to non-`0`, **and** you set `Enable skin overlays` to `true`, the skin will be displayed in the shop view.
- `Server Rewards` -- Controls integration with the Server Rewards plugin.
  - `Enabled` (`true` or `false`) -- Same as for Economics.
  - `Item short name` -- Same as for Economics.
  - `Item skin ID` -- Same as for Economics.
- `Override item max stack sizes (shortname: amount)` -- This section allows you to override the max stack size of items that players can get when purchasing items, by item short name. This is intended to allow players to receive larger stacks of items from vending machines than they could receive from other sources. For example, if the max stack size of wood is `5000`, configuring a maximum of `10000` here will allow the player to acquire a single wood item with stack size `10000` (granted the vending machine has enough wood in stock).
  - This feature only applies to vending machines customized by this plugin.
  - This feature might not work with every stack size plugin. Worst case, editing these settings may have no effect.
  - I don't recommend this feature for most servers. It was added on request because the plugin fixes a vanilla bug where players can acquire larger stack sizes of items from vending machines after server reboot. Some server owners wanted that behavior reintroduced in a manner that is more consistent.

Example of overriding stack sizes:

```json
"Override item max stack sizes (shortname: amount)": {
  "wood": 10000,
  "stones": 5000
}
```

## Localization

## FAQ

#### How do I buy items faster (no transaction delay)?

Install the [Instant Buy](https://umod.org/plugins/instant-buy) plugin. It's compatible.

#### How do I make items restock instantly?

Install the [Vending In Stock](https://umod.org/plugins/vending-in-stock) plugin. It's compatible.

Alternatively, you can change restock speed per item, per vending machine by changing the "Seconds Between Refills" value in the corresponding item's note. Setting that value to `0` will cause that item to be restocked instantly when purchased.

#### How do I allow players to purchase more items in bulk?

The number of items you can purchase at once is determined by the vending machine's stock. By default, vending machines stock enough merchandise for 10 purchases, though more may be stocked in some cases (see below for that question). You can change the max stock per item, per vending machine by changing the "Max Stock" value in the corresponding item's note.

Note that some items, especially items that have a condition bar, cannot be purchased in bulk due to client-side limitations. Plugins cannot easily solve this limitation. As a workaround, you can create multiple listings for the same item (e.g., 1 drone for 10 scrap, 10 drones for 100 scrap, 100 drones for 1000 scrap).

#### Why do some items show more stock than configured?

This can happen for vending machines that sell an item for multiple amounts (e.g., scrap to **wood**, and stones to **wood**). This happens because each vending machine uses a single stock container for all items it sells, so it will try to stock the amount required for whichever sale offer requires the most stock.

If you want all items to show the same quantity in stock (e.g., 10 in stock), you can alter the sell amount and currency amount so that the sell amount matches other listings of the same item.

Example problem:

- 1000 wood for 20 scrap (shows 10 in stock)
- 500 wood for 150 stones (shows **20** in stock)

Example solution A (stocks 10k wood total, same as original):

- 1000 wood for 20 scrap (shows 10 in stock)
- **1000** wood for **300** stones (shows 10 in stock)

Example solution B (stocks 5k wood total, half original):

- **500** wood for **10** scrap (shows 10 in stock)
- 500 wood for 150 stones (shows 10 in stock)

#### Why is stock different than vanilla?

This can happen for vending machines that sell an item for multiple amounts (e.g., scrap to **wood**, and stones to **wood**). This happens because vanilla stocking logic has inconsistencies which are fixed in the plugin's stocking logic.

#### Can I sell more than 7 items?

No. At most 7 items can be sold per vending machine. It's not possible to sell more due to UI limitations in the vanilla game. A pagination feature might be implemented in future versions of the plugin.

#### How do I display custom item names?

There is currently no way to display custom item names, but this feature is planned. However, the plugin does store custom item names, and correctly sets those names on items that players purchase.

#### How do I setup custom monuments?

If you are using a map with custom monuments, first ask yourself, are you going to use the custom monuments on other maps?

- If the answer is **no**, you don't need Monument Finder, and you can simply use the Map data provider feature of this plugin.
- If the answer is **yes**, you should install Monument Finder and configure custom bounds. Continue reading below.

In order for Custom Vending Setup to know which monument to save vending machine customizations relative to, it needs to be aware of the location and size of all monuments. Custom Vending Setup delegates this responsibility to the [Monument Finder](https://umod.org/plugins/monument-finder) plugin. Monument Finder has hard-coded bounds for most vanilla monuments, but for custom monuments, you will most likely have to configure it as there is no reliable way for it to guess the location and size of custom monuments. Please see the section titled [How to set up custom monuments](https://umod.org/plugins/monument-finder#how-to-set-up-custom-monuments) in the Monument Finder documentation.

Once you have configured the custom monument via Monument Finder, interact with the vending machine and click the Edit button to confirm that the vending machine is using a Monument data provider (**not** a Map data provider). If you still see a Map data provider, that could be due to one of two reasons.
- If you have previously saved customizations for the vending machine using a Map data provider, you need to Reset the vending machine to allow it to switch to a Monument data provider.
- If the monument bounds do not envelope the vending machine's position, you need to reconfigure the monument bounds and try again.

## Developer API

#### API_IsCustomized

```cs
bool API_IsCustomized(NPCVendingMachine vendingMachine)
```

Returns `true` if the vending machine has been customized by this plugin, else `false`.

#### API_RefreshDataProvider

```cs
void API_RefreshDataProvider(NPCVendingMachine vendingMachine)
```

Removes the vending machine's currently assigned data provider and calls the `OnCustomVendingSetupDataProvider` hook again. If no plugin responds to that hook, the vending machine will fall back to using a monument based data provider.

## Developer Hooks

#### OnCustomVendingSetup

```cs
object OnCustomVendingSetup(NPCVendingMachine vendingMachine)
```

- Called when this plugin wants to internally register a monument vending machine to allow it to be edited
- Returning `false` will prevent the vending machine from being edited
- Returning `null` will allow the vending machine to be edited

#### OnCustomVendingSetupGiveSoldItem

```cs
void OnCustomVendingSetupGiveSoldItem(NPCVendingMachine vendingMachine, Item item, BasePlayer player)
```

- Called when this plugin is going to override the logic for giving an item to a player from a vending machine
- This is useful for plugins that want to observe a player receiving an item from a vending machine, in conjunction with `OnNpcGiveSoldItem`, since using only `OnNpcGiveSoldItem` by itself can result in the item's `amount` being incorrect as that hook may be called on your plugin after Custom Vending Setup has given the item to a player which can merge the item with another item already in the player's inventory

Example usage:
```cs
[PluginReference]
Plugin CustomVendingSetup;

void OnCustomVendingSetupGiveSoldItem(NPCVendingMachine vendingMachine, Item item, BasePlayer player)
{
    // Run some logic to count the purchase
}

void OnNpcGiveSoldItem(NPCVendingMachine vendingMachine, Item item, BasePlayer player)
{
    // Don't count the purchase if CustomVendingSetup is controlling the vending machine,
    // since `OnCustomVendingSetupGiveSoldItem` will also be called in that case
    if (CustomVendingSetup?.Call("API_IsCustomized", vendingMachine) is true)
        return;

    // Run some logic to count the purchase
}
```

#### OnCustomVendingSetupDataProvider

```cs
Dictionary<string, object> OnCustomVendingSetupDataProvider(NPCVendingMachine vendingMachine)
```

- Called when this plugin wants to internally register a vending machine, before associating it with a built-in map or monument data provider
- Returning a valid dictionary will override where the plugin retrieves/saves data
- Returning `null` will result in the default behavior

The dictionary should contain the following keys.
- `"Plugin"` -- Your plugin. Providing this allows administrators to see that your plugin is a data provider for a given vending machine.
  - Type: `Oxide.Core.Plugins.Plugin`
- `"GetData"` -- A method that Custom Vending Setup will call to retrieve data for this vending machine.
  - Type: `System.Func<JObject>`
- `"SaveData"` -- A method that Custom Vending Setup will call to save data after the vending machine offers have been edited or reset.
  - Type: `System.Action<JObject>`

If you intend for multiple vending machines to share the same data, make sure you return the same dictionary instance for all of them.

Example:

```cs
class PluginData
{
    [JsonProperty("VendingProfile")]
    public object VendingProfile;
}

PluginData _pluginData;

void Init()
{
    _pluginData = Interface.Oxide.DataFileSystem.ReadObject<PluginData>("Test_VendingProfile") ?? new PluginData();
}

Dictionary<string, object> OnCustomVendingSetupDataProvider(NPCVendingMachine vendingMachine)
{
    if (vendingMachine.net.ID == 123456)
    {
        return new Dictionary<string, object>
        {
            ["Plugin"] = this,
            ["GetData"] = new System.Func<JObject>(() =>
            {
                return _pluginData.VendingProfile as JObject;
            }),
            ["SaveData"] = new System.Action<JObject>(data =>
            {
                _pluginData.VendingProfile = data;
                Interface.Oxide.DataFileSystem.WriteObject("Test_VendingProfile", _pluginData);
            }),
        };
    }

    return null;
}
```

## Credits

- **misticos**, the original author of this plugin (v1)
