## Data Providers

**This feature is currently experimental.**

Data Providers allow other plugins to decide where Custom Vending Setup will retrieve and save data for a given vending machine.

#### Hook: OnCustomVendingSetupDataProvider

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

#### Example

```cs
private class PluginData
{
    [JsonProperty("CustomVendingData")]
    public object CustomVendingData;
}

private PluginData _pluginData;

private void Init()
{
    _pluginData = Interface.Oxide.DataFileSystem.ReadObject<PluginData>("Test_CustomVendingData") ?? new PluginData();
}

private Dictionary<string, object> OnCustomVendingSetupDataProvider(NPCVendingMachine vendingMachine)
{
    if (vendingMachine.net.ID == 123456)
    {
        return new Dictionary<string, object>
        {
            ["GetData"] = new System.Func<JObject>(() =>
            {
                return _pluginData.CustomVendingData as JObject;
            }),
            ["SaveData"] = new System.Action<JObject>(data =>
            {
                _pluginData.CustomVendingData = data;
                Interface.Oxide.DataFileSystem.WriteObject("Test_CustomVendingData", _pluginData);
            }),
        };
    }

    return null;
}
```
