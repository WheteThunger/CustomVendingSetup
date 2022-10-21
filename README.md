## Video tutorial

[![Video Tutorial](https://img.youtube.com/vi/oiKKByGV_i0/mqdefault.jpg)](https://www.youtube.com/watch?v=oiKKByGV_i0)

## Features

- Allows customizing items sold at monument vending machines
- Supports vanilla monuments, custom monuments, train tunnels, and underwater labs
- Saves customizations for future restarts and wipes, based on the vending machine's relative position to the monument
- Synchronizes edits with copies of the vending machines at duplicate monuments
- Compatible with NPC vending machines spawned at monuments by plugins such as [Monument Addons](https://umod.org/plugins/monument-addons)
- Allows disabling vending machine map markers
- Allows disabling delivery drone access
- Supports blueprints, as well as items with custom skins and names
- Supports attachments and other child items
- Supports ammo type and amount

## Required plugins

- [Monument Finder](https://umod.org/plugins/monument-finder) -- Simply install. No configuration needed, except for custom monuments.

## How it works

When you open an NPC vending machine at a monument, if you have permission, you will see an edit button. Clicking that edit button will reveal a container UI where you can customize the vending machine.

- Change which items are sold, and their prices, by adding and removing items from the container
- Change display order by rearranging items in the container
- Change stock settings by editing the note next to each item
- Toggle whether the map marker is enabled by clicking on the broadcast icon (green = on, gray = off)
- Toggle whether delivery drones can access the vending machine by clicking on the drone icon (green = on, gray = off)
- Change the shop name by clicking on the bottom-right note and editing its contents (supports multiple lines)
- Save the changes by clicking the "SAVE" button

## Permissions

- `customvendingsetup.use` -- Allows editing NPC vending machines at monuments.

## Screenshots

![](https://raw.githubusercontent.com/WheteThunger/CustomVendingSetup/master/ShopView.png)

![](https://raw.githubusercontent.com/WheteThunger/CustomVendingSetup/master/ContainerViewStock.png)

![](https://raw.githubusercontent.com/WheteThunger/CustomVendingSetup/master/ContainerViewName.png)

## Configuration

Default configuration:

```json
{
  "Shop UI settings": {
    "Enable skin overlays": false
  },
  "Override item max stack sizes (shortname: amount)": {}
}
```

- `Shop UI settings`
  - `Enable skin overlays` (`true` or `false`) -- While `true`, skin images will be overlayed on top of items when needed. For example, to display currency skin.
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

```json
{
  "Button.Save": "SAVE",
  "Button.Cancel": "CANCEL",
  "Button.Edit": "EDIT",
  "Button.Reset": "RESET",
  "Info.ForSale": "FOR SALE",
  "Info.Cost": "COST",
  "Info.Settings": "SETTINGS",
  "Settings.RefillMax": "Max Stock",
  "Settings.RefillDelay": "Seconds Between Refills",
  "Settings.RefillAmount": "Refill Amount",
  "Error.CurrentlyBeingEdited": "That vending machine is currently being edited by {0}."
}
```

## FAQ

#### Can I sell more than 7 items?

No. At most 7 items can be sold per vending machine. It's not possible to sell more due to UI limitations in the vanilla game. A pagination feature might be implemented in future versions of the plugin.

#### How do I setup custom monuments?

As a prerequisite, the custom monument must use the monument marker prefab and have a unique name. Then, you must configure the monument's bounds in [Monument Finder](https://umod.org/plugins/monument-finder) to envelope the monument so that Custom Vending Setup can accurately determine whether a given vending machine is within that monument. Please see the Monument Finder plugin documentation for further guidance.

#### Why do some items show more stock than configured?

This can happen for vending machines that sell the same item for multiple amounts. For example, if a vending machine sells `1000` stones for `50` scrap, and `500` stones for `1000 wood` wood, if configured to stock `10` for both, the total amount stocked will be `10000` stones, meaning the player can purchase `10` purchases of the `1000`, or `20` purchases of `500`. This happens because each vending machine uses a single stock container for all items it sells.

If you want to avoid this behavior, increase the restock amount for items sold at smaller amounts, or decrease the restock amount for items sold at higher amounts. For example, if selling `500` stones with `20` amount, sell `1000` stones for `10` amount.

#### How do I display custom item names?

There is currently no way to display custom item names, but this feature is planned.

## Developer API

#### API_IsCustomized

```csharp
bool API_IsCustomized(NPCVendingMachine vendingMachine)
```

Returns `true` if the vending machine has been customized by this plugin, else `false`.

#### API_RefreshDataProvider

```csharp
void API_RefreshDataProvider(NPCVendingMachine vendingMachine)
```

Removes the vending machine's currently assigned data provider and calls the `OnCustomVendingSetupDataProvider` hook again. If no plugin responds to that hook, the vending machine will fall back to using a monument based data provider.

## Developer Hooks

#### OnCustomVendingSetup

```csharp
bool? OnCustomVendingSetup(NPCVendingMachine vendingMachine)
```

- Called when this plugin wants to internally register a monument vending machine to allow it to be edited
- Returning `false` will prevent the vending machine from being edited
- Returning `null` will allow the vending machine to be edited

#### OnCustomVendingSetupDataProvider

```csharp
Dictionary<string, object> OnCustomVendingSetupDataProvider(NPCVendingMachine vendingMachine)
```

- Called when this plugin wants to internally register a vending machine, before checking if it's at a monument
- Returning a valid dictionary will override where the plugin retrieves/saves data
- Returning `null` will result in the default behavior

The dictonary should contain the following keys.
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