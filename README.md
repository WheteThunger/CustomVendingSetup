## Features

- Allows editing sell orders of monument vending machines
- Supports vanilla monuments, custom monuments, train tunnels, and underwater labs (via Monument Finder)
- Saves sell orders for future restarts and wipes, based on the vending machine's relative position to the monument
- Synchronizes orders with copies of the vending machines at duplicate monuments
- Compatible with NPC vending machines spawned at monuments by plugins such as [Monument Addons](https://umod.org/plugins/monument-addons)
- Supports items with custom skins and names

## Required plugins

- [Monument Finder](https://umod.org/plugins/monument-finder) -- Simply install. No configuration needed.

## How it works

When you open an NPC vending machine at a monument, if you have permission, you will see an edit button. Clicking that edit button will reveal a container UI where you can customize the vending machine.

- Change which items are sold, and their prices, by adding and removing items from the container
- Rearrange items in the container to determine their order in the shop view
- Toggle whether the map marker is enabled by clicking on the broadcast icon (green = on, gray = off)
- Change the shop name by clicking on the note and editing its contents (supports multiple lines)
- Save the changes by clicking the "SAVE" button

## Permissions

- `customvendingsetup.use` -- Allows editing NPC vending machines at monuments.

## Screenshots

![](https://raw.githubusercontent.com/WheteThunger/CustomVendingSetup/master/ShopView.png)

![](https://raw.githubusercontent.com/WheteThunger/CustomVendingSetup/master/ContainerView.png)

## Localization

```json
{
  "Button.Save": "SAVE",
  "Button.Cancel": "CANCEL",
  "Button.Edit": "EDIT",
  "Button.Reset": "RESET",
  "Info.ForSale": "FOR SALE",
  "Info.Cost": "COST",
  "Info.Name": "NAME",
  "Error.CurrentlyBeingEdited": "That vending machine is currently being edited by {0}."
}
```

## Troubleshooting

If this plugin isn't detecting a monument vending machine, make sure it's an NPC vending machine, and that it is within the monument bounds as determined by [Monument Finder](https://umod.org/plugins/monument-finder). You can change the size of monument bounds using Monument Finder if needed.

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
