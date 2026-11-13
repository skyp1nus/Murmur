using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;

namespace Murmur.App.Tests;

/// <summary>
/// #102 / #108: a binding on a property that is TwoWay by default (<c>TextBox.Text</c>, <c>ToggleButton.IsChecked</c>, <c>Run.Text</c>,
/// <c>RangeBase.Value</c>, <c>SelectingItemsControl.SelectedItem</c>, <c>Popup.IsOpen</c>, ...) writes its value back into the view model.
/// A <c>Run</c> bound with a StringFormat sent its formatted text back, which raised the change, which formatted the text again, until the
/// stack overflowed (the clock of the recording details). Under the JIT the generated accessor cannot call a private setter
/// (<c>MethodAccessException</c>, swallowed by the binding), so only the Native AOT build writes back, and no headless test sees it: these
/// checks of the markup are the JIT-side guard, the <c>--probe-open-latest</c> run of the published binary in CI the AOT-side one.
/// </summary>
public sealed class BindingModeTests
{
    private static readonly Lazy<List<BindingSite>> Sites = new(AxamlBindingScanner.Scan);

    [Fact]
    public void EveryBindingOnATwoWayByDefaultProperty_StatesItsMode()
    {
        var offenders = Sites.Value.Where(s => s.TwoWayByDefault && !s.StatesMode).Select(s => s.Location).ToList();

        Assert.True(
            offenders.Count == 0,
            "These properties bind TwoWay by default and write back into the view model (in the AOT build, even into a private setter). "
            + "State Mode=OneWay for display-only values, Mode=TwoWay for edits:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void EveryExplicitTwoWayBinding_TargetsAPublicSetter()
    {
        var offenders = Sites.Value
            .Where(s => s.IsExplicitTwoWay && s.Target is not null && !s.HasPublicSetter)
            .Select(s => s.Location + " -> " + s.ViewModel?.Name + "." + s.Target!.Name)
            .ToList();

        Assert.True(offenders.Count == 0, "Mode=TwoWay needs a public setter on the view model (use [ObservableProperty] or Mode=OneWay):\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void ExplicitTwoWayBindings_ResolveTheirViewModelProperty()
    {
        // A path that cannot be resolved (an ElementName source, an indexer, a missing x:DataType) escapes the check above; keep those visible.
        var unresolved = Sites.Value.Where(s => s.IsExplicitTwoWay && s.Target is null).Select(s => s.Location).ToList();

        Assert.True(unresolved.Count == 0, "These Mode=TwoWay bindings could not be resolved to a view-model property:\n" + string.Join('\n', unresolved));
    }

    [Fact]
    public void EveryBoundElement_ResolvesToAnAvaloniaOrAppType()
    {
        // An element whose type is unknown cannot be asked for its TwoWay properties, so its bindings would pass unchecked.
        var unknown = Sites.Value.Where(s => !s.ElementResolved).Select(s => s.Location).ToList();

        Assert.True(unknown.Count == 0, "The type of these elements could not be resolved:\n" + string.Join('\n', unknown));
    }

    [Fact]
    public void TheScanFindsTheBindingsItGuards()
    {
        // Guards the guard: a broken scan (no types resolved, no files read) must not pass as "nothing to report".
        var twoWayDefault = Sites.Value.Where(s => s.TwoWayByDefault).ToList();
        Assert.True(twoWayDefault.Count > 40, $"only {twoWayDefault.Count} TwoWay-default bindings found");
        Assert.Contains(twoWayDefault, s => s is { Element: "TextBox", Property: "Text" });
        Assert.Contains(twoWayDefault, s => s.Property == "IsChecked");
        Assert.Contains(twoWayDefault, s => s is { Element: "Run", Property: "Text" });
        Assert.Contains(twoWayDefault, s => s is { Element: "ProgressBar", Property: "Value" });
        Assert.Contains(twoWayDefault, s => s.Property == "SelectedItem");
        Assert.True(Sites.Value.Count(s => s.IsExplicitTwoWay && s.Target is not null) > 40, "the view-model paths of the TwoWay bindings were not resolved");
    }

    [Theory]
    [InlineData(typeof(TextBox), "Text")]
    [InlineData(typeof(CheckBox), "IsChecked")]
    [InlineData(typeof(RadioButton), "IsChecked")]
    [InlineData(typeof(ToggleSwitch), "IsChecked")]
    [InlineData(typeof(ComboBox), "SelectedItem")]
    [InlineData(typeof(ComboBox), "SelectedIndex")]
    [InlineData(typeof(ListBox), "SelectedItem")]
    [InlineData(typeof(TabControl), "SelectedIndex")]
    [InlineData(typeof(Expander), "IsExpanded")]
    [InlineData(typeof(Slider), "Value")]
    [InlineData(typeof(ProgressBar), "Value")] // OneWay on ProgressBar itself in Avalonia 12, but TwoWay where RangeBase declares it
    [InlineData(typeof(ScrollBar), "Value")]
    [InlineData(typeof(NumericUpDown), "Value")]
    [InlineData(typeof(AutoCompleteBox), "Text")]
    [InlineData(typeof(DatePicker), "SelectedDate")]
    [InlineData(typeof(TimePicker), "SelectedTime")]
    [InlineData(typeof(Run), "Text")]
    public void TwoWayByDefault_ComesFromAvaloniaMetadata(Type control, string property) =>
        Assert.Contains(AxamlBindingScanner.TwoWayByDefault(control), p => p.Name == property);

    [Theory]
    [InlineData(typeof(TextBlock), "Text")]
    [InlineData(typeof(Border), "Background")]
    [InlineData(typeof(Button), "IsEnabled")]
    [InlineData(typeof(Popup), "IsOpen")] // OneWay by default in Avalonia 12, so a bare binding does not write back
    [InlineData(typeof(SplitView), "IsPaneOpen")]
    public void TwoWayByDefault_LeavesOutOneWayProperties(Type control, string property) =>
        Assert.DoesNotContain(AxamlBindingScanner.TwoWayByDefault(control), p => p.Name == property);

    [Theory]
    [InlineData("{Binding PositionText, StringFormat='{}{0} '}", null)]
    [InlineData("{Binding KindText}", null)]
    [InlineData("{Binding PositionText, Mode=TwoWay}", "TwoWay")]
    [InlineData("{Binding PositionText, Mode=OneWayToSource}", "OneWayToSource")]
    [InlineData("{Binding PositionText, Mode=OneWay, StringFormat='{}{0} '}", "OneWay")]
    [InlineData("{CompiledBinding Title, Mode=OneTime}", "OneTime")]
    [InlineData("{Binding Path=Title, Converter={StaticResource Fmt}, ConverterParameter='a, Mode=TwoWay'}", null)]
    [InlineData("{Binding Title, Converter={x:Static Foo.Bar}, Mode=OneWay}", "OneWay")]
    public void ParseArguments_ReadsTheModeOfTheMarkup(string text, string? expected)
    {
        var (_, named) = AxamlBindingScanner.ParseArguments(text);
        Assert.Equal(expected, named.GetValueOrDefault("Mode"));
    }

    [Theory]
    [InlineData("{Binding Foo}", true)]
    [InlineData("{CompiledBinding Foo}", true)]
    [InlineData("{ReflectionBinding Foo}", true)]
    [InlineData("{Binding}", true)]
    [InlineData("{TemplateBinding Foo}", false)]
    [InlineData("{DynamicResource Foo}", false)]
    [InlineData("{BindingFoo}", false)]
    [InlineData("Binding Foo", false)]
    public void IsBinding_RecognisesBindingExtensions(string text, bool expected) => Assert.Equal(expected, AxamlBindingScanner.IsBinding(text));
}
