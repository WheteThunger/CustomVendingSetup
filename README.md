## Video tutorial

[![Video Tutorial](https://img.youtube.com/vi/oiKKByGV_i0/mqdefault.jpg)](https://www.youtube.com/watch?v=oiKKByGV_i0)

## Features

- Allows customizing items sold at monument vending machines
- Supports vanilla monuments, custom monuments, train tunnels, and underwater labs (via Monument Finder)
- Saves customizations for future restarts and wipes, based on the vending machine's relative position to the monument
- Synchronizes edits with copies of the vending machines at duplicate monuments
- Compatible with NPC vending machines spawned at monuments by plugins such as [Monument Addons](https://umod.org/plugins/monument-addons)
- Supports blueprints, as well as items with custom skins and names

## Required plugins

- [Monument Finder](https://umod.org/plugins/monument-finder) -- Simply install. No configuration needed.

## How it works

When you open an NPC vending machine at a monument, if you have permission, you will see an edit button. Clicking that edit button will reveal a container UI where you can customize the vending machine.

- Change which items are sold, and their prices, by adding and removing items from the container
- Change display order by rearranging items in the container
- Change stock settings by editing the note next to each item
- Toggle whether the map marker is enabled by clicking on the broadcast icon (green = on, gray = off)
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
  "Override item max stack sizes (shortname: amount)": {}
}
```

- `Override item max stack sizes (shortname: amount)` -- This section allows you to override the max stack size that players can get when purchasing items, by item short name. This is intended to allow players to get larger stacks of items from vending machines than they could get from other sources.
  - This feature only applies to vending machines customized by this plugin.
  - This feature might not work with every stack size plugin. Worst case, editing these settings may have no effect.

Example of overriding stack sizes:

```json
{
  "Override item max stack sizes (shortname: amount)": {
    "wood": 10000,
    "stones": 5000
  }
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

No. At most 7 items can be sold per vending machine. It's not possible to sell more due to UI limitations in the vanilla game.

#### How do I setup custom monuments?

As a prerequisite, the custom monument must use the monument marker prefab and have a unique name. Then, you must configure the monument's bounds in [Monument Finder](https://umod.org/plugins/monument-finder) to envelope the monument so that Custom Vending Setup can accurately determine whether a given vending machine is within that monument.

## Developer API

#### API_IsCustomized

```csharp
bool API_IsCustomized(NPCVendingMachine vendingMachine)
```

- Returns `true` if the vending machine has been customized by this plugin, else `false`.

## Developer Hooks

#### OnCustomVendingSetup

```csharp
bool? OnCustomVendingSetup(NPCVendingMachine vendingMachine)
```

- Called when this plugin wants to internally register a monument vending machine to allow it to be edited
- Returning `false` will prevent the vending machine from being edited
- Returning `null` will allow the vending machine to be edited

## Credits

- **misticos**, the original author of this plugin (v1)
