using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using Rondel.Core.Localization;

namespace Rondel.Windows.UI;

/// <summary>Binding source for XAML; raises an indexer change when the language switches.</summary>
public sealed class LocSource : INotifyPropertyChanged
{
    LocSource() => Loc.Changed += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));

    public static LocSource Instance { get; } = new();

    public string this[string key] => Loc.Get(key);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Usage: <c>Text="{l:Loc Settings.Title}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = LocSource.Instance, Mode = BindingMode.OneWay }
            .ProvideValue(serviceProvider);
}
