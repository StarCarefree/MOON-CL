using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCL;

/// <summary>
/// Transitional presentation adapter. WPF pages retain their business/lifecycle logic behind the WebView;
/// only typed, explicitly supported controls are exposed to the HTML presentation layer.
/// </summary>
internal sealed class WebPageProjection
{
    private sealed record Identity(string Value);
    private readonly ConditionalWeakTable<DependencyObject, Identity> _identities = new();
    private readonly Dictionary<string, Action<string, JsonNode?>> _actions = new();
    private int _nextId;
    private readonly ConditionalWeakTable<BitmapSource, Identity> _images = new();

    public JsonObject Capture(FormMain form)
    {
        _actions.Clear();
        var modal = form.PanMsgBackground.Visibility == Visibility.Visible;
        // Register actions only for the active modal, never the obscured page.
        var left = Read(form.PanMainLeft.Child, interactive: !modal);
        var right = Read(form.PanMainRight.Child, interactive: !modal);
        var extras = Read(form.PanExtraButtons, interactive: !modal);
        var dialogs = modal ? Read(form.PanMsg) : null;
        return new JsonObject
        {
            ["type"] = "page",
            ["page"] = (int)(FormMain.PageType)form.pageCurrent,
            ["sub"] = (int)form.PageCurrentSub,
            ["contentState"] = (form.PanMainRight.Child as MyPageRight)?.PageState.ToString() ?? "Empty",
            ["title"] = form.pageStack.Count > 0 ? form.LabTitleInner.Text : form.Title,
            ["canBack"] = form.pageStack.Count > 0,
            ["left"] = left,
            ["right"] = right,
            ["extras"] = extras,
            ["dialogs"] = dialogs,
            ["hints"] = Read(form.PanHint, interactive: false)
        };
    }

    public bool Act(string id, string operation, JsonNode? value)
    {
        if (!_actions.TryGetValue(id, out var action)) return false;
        action(operation, value);
        return true;
    }

    private string Id(DependencyObject control) =>
        _identities.GetValue(control, _ => new Identity("c" + ++_nextId)).Value;

    private void Bind(FrameworkElement element, bool interactive, Action<string, JsonNode?> action)
    {
        if (!interactive) return;
        _actions[Id(element)] = (op, value) =>
        {
            if (!element.IsEnabled || element.Visibility != Visibility.Visible || !element.IsLoaded) return;
            // Visibility may change between a snapshot and a queued browser action.
            for (DependencyObject? parent = element; parent is not null; parent = LogicalTreeHelper.GetParent(parent))
                if (parent is UIElement ui && (ui.Visibility != Visibility.Visible || !ui.IsEnabled)) return;
            action(op, value);
        };
    }

    private JsonObject? Read(UIElement? element, int depth = 0, bool interactive = true)
    {
        if (element is not FrameworkElement control || control.Visibility != Visibility.Visible || depth > 60)
            return null;
        if (control.GetType().Name.StartsWith("MyVirtualizingElement", StringComparison.Ordinal))
        {
            Bind(control, interactive, (op, _) => { if (op == "materialize") MyVirtualizingElement.TryInit(control); });
            return new JsonObject { ["id"] = Id(control), ["kind"] = "virtual", ["enabled"] = control.IsEnabled };
        }
        var node = new JsonObject
        {
            ["id"] = Id(control),
            ["kind"] = "group",
            ["name"] = control.Name,
            ["enabled"] = control.IsEnabled,
            ["tip"] = control.ToolTip is string tip ? tip : "",
            ["row"] = Grid.GetRow(control),
            ["column"] = Grid.GetColumn(control),
            ["rowSpan"] = Grid.GetRowSpan(control),
            ["columnSpan"] = Grid.GetColumnSpan(control)
        };
        void Click(Action action)
        {
            node["kind"] = "button";
            Bind(control, interactive, (op, _) => { if (op == "click") action(); });
        }
        void Children(IEnumerable<UIElement> elements)
        {
            var array = new JsonArray();
            foreach (var child in elements.ToArray())
                if (Read(child, depth + 1, interactive) is { } item) array.Add(item);
            node["children"] = array;
        }

        switch (control)
        {
            case MyButton button:
                node["text"] = InlineText(button.Inlines, button.Text);
                node["accent"] = button.ColorType == MyButton.ColorState.Highlight;
                Click(button.WebClick);
                break;
            case MyTextButton button:
                node["text"] = button.Text;
                Click(button.WebClick);
                break;
            case MyIconButton button:
                node["text"] = IconLabel(control);
                Click(button.WebClick);
                break;
            case MyIconTextButton button:
                node["text"] = TextProperty(button, "Text");
                Click(button.WebClick);
                break;
            case MyExtraTextButton button:
                node["text"] = TextProperty(button, "Text");
                Click(button.WebClick);
                break;
            case MyExtraButton button:
                node["text"] = IconLabel(control);
                Click(() => button.WebClick());
                break;
            case MyRadioButton radio:
                node["text"] = radio.Text;
                node["selected"] = radio.Checked;
                Click(radio.WebClick);
                break;
            case MyCheckBox check:
                node["kind"] = "check";
                node["text"] = check.Text;
                node["checked"] = check.Checked;
                Bind(control, interactive, (op, value) => { if (op == "check" && TryBool(value, out var flag)) check.SetChecked(flag, true); });
                break;
            case MyRadioBox radio:
                node["kind"] = "radio";
                node["text"] = radio.Text;
                node["checked"] = radio.Checked;
                node["group"] = Id(radio.Parent ?? radio);
                Bind(control, interactive, (op, _) => { if (op == "check") radio.SetChecked(true, true); });
                break;
            case MySlider slider:
                node["kind"] = "range";
                node["value"] = slider.Value;
                node["max"] = slider.MaxValue;
                Bind(control, interactive, (op, value) => { if (op == "value" && TryInt(value, out var number)) slider.Value = Math.Clamp(number, 0, slider.MaxValue); });
                break;
            case MySearchBox search:
                node["kind"] = "search";
                node["value"] = search.Text;
                node["placeholder"] = search.HintText;
                Bind(control, interactive, (op, value) =>
                {
                    if (op == "text" && TryText(value, out var text)) search.Text = text;
                    if (op == "search") search.WebSearch();
                });
                break;
            case MyListItem item:
                // Some instance items install their buttons/content only on hover in the native view.
                item.RefreshColor(item, EventArgs.Empty);
                node["kind"] = "item";
                node["text"] = InlineText(item.Inlines, item.Title);
                node["info"] = item.Info;
                node["selected"] = item.Checked;
                Bind(control, interactive, (op, _) => { if (op == "click") item.WebClick(); });
                Children(item.Buttons?.Cast<UIElement>() ?? []);
                break;
            case MyCompItem item:
                node["kind"] = "item";
                node["text"] = item.Title + " " + item.SubTitle;
                node["info"] = item.Description;
                node["enabled"] = item.IsEnabled && item.CanInteraction;
                Bind(control, interactive, (op, _) => { if (op == "click") item.WebClick(); });
                Children(item.PanButtons.Children.Cast<UIElement>());
                break;
            case MyLocalCompItem item:
                node["kind"] = "item";
                node["text"] = item.Title + " " + item.SubTitle;
                node["selected"] = item.Checked;
                Bind(control, interactive, (op, _) => { if (op == "click") item.WebClick(); });
                // Render its real action buttons without duplicating the internal labels/chrome.
                Children(FindButtons(item));
                break;
            case MyCollapseBar collapse:
                node["kind"] = "card";
                node["text"] = collapse.Title;
                node["collapsible"] = true;
                node["collapsed"] = collapse.IsCollapsed;
                Bind(control, interactive, (op, _) => { if (op == "toggle") collapse.IsCollapsed = !collapse.IsCollapsed; });
                Children(collapse.IsCollapsed ? [] : collapse.ContentPanel.Children.Cast<UIElement>());
                break;
            case MyCard card:
                node["kind"] = "card";
                node["text"] = card.Title;
                node["collapsible"] = card.CanSwap || card.SwapControl is not null;
                node["collapsed"] = card.IsSwapped;
                Bind(control, interactive, (op, _) => { if (op == "toggle") card.WebToggle(); });
                // First three children are the native shadow, border and card title.
                Children(card.IsSwapped ? [] : card.Children.Cast<UIElement>().Skip(3));
                break;
            case MyHint hint:
                node["kind"] = "hint";
                node["text"] = InlineText(hint.Inlines, hint.Text);
                break;
            case MyLoading loading:
                node["text"] = loading.LabText.Text;
                node["loading"] = true;
                Click(loading.WebClick);
                break;
            case TextBox text:
                node["kind"] = text.AcceptsReturn ? "textarea" : "input";
                node["value"] = text.Text;
                node["readOnly"] = text.IsReadOnly;
                node["maxLength"] = text.MaxLength;
                if (text is MyTextBox validated)
                {
                    node["placeholder"] = validated.HintText;
                    node["error"] = validated.ValidateResult;
                }
                Bind(control, interactive, (op, value) =>
                {
                    if (op == "text" && !text.IsReadOnly && TryText(value, out var input) && (text.MaxLength == 0 || input.Length <= text.MaxLength))
                        text.SetCurrentValue(TextBox.TextProperty, input);
                    if (op == "enter") RaiseEnter(text);
                });
                break;
            case PasswordBox password:
                node["kind"] = "password";
                // Never send the saved credential back to the browser.
                Bind(control, interactive, (op, value) => { if (op == "text" && TryText(value, out var input)) password.Password = input; });
                break;
            case ComboBox combo:
                node["kind"] = combo.IsEditable ? "combo" : "select";
                node["value"] = combo.Text;
                node["index"] = combo.SelectedIndex;
                var options = new JsonArray();
                for (var i = 0; i < combo.Items.Count; i++)
                {
                    var option = combo.Items[i];
                    options.Add(new JsonObject
                    {
                        ["text"] = option is ContentControl content ? ContentText(content.Content) : option?.ToString(),
                        ["index"] = i,
                        ["enabled"] = option is not UIElement ui || ui.IsEnabled,
                        ["visible"] = option is not UIElement visible || visible.Visibility == Visibility.Visible
                    });
                }
                node["options"] = options;
                Bind(control, interactive, (op, value) =>
                {
                    if (op == "select" && TryInt(value, out var index) && index >= 0 && index < combo.Items.Count &&
                        (combo.Items[index] is not UIElement option || (option.IsEnabled && option.Visibility == Visibility.Visible))) combo.SelectedIndex = index;
                    if (op == "text" && combo.IsEditable && TryText(value, out var input)) combo.Text = input;
                });
                break;
            case ToggleButton toggle:
                node["kind"] = toggle is RadioButton ? "radio" : "check";
                node["text"] = ContentText(toggle.Content);
                node["checked"] = toggle.IsChecked;
                if (toggle is RadioButton grouped)
                    node["group"] = grouped.GroupName.Length > 0 ? "group:" + grouped.GroupName : Id(LogicalTreeHelper.GetParent(toggle) ?? toggle);
                Bind(control, interactive, (op, value) => { if (op == "check" && TryBool(value, out var flag)) toggle.IsChecked = flag; });
                break;
            case ButtonBase button:
                node["text"] = ContentText(button.Content);
                Click(() => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button)));
                break;
            case TextBlock text:
                node["kind"] = "text";
                node["text"] = InlineText(text.Inlines, text.Text);
                node["heading"] = text.FontSize >= 17 || text.FontWeight == FontWeights.Bold;
                break;
            case Image image when image.Source is BitmapSource bitmap:
                node["kind"] = "image";
                node["src"] = ImageData(bitmap);
                node["text"] = control.ToolTip as string ?? "";
                break;
            case RichTextBox rich:
                node["kind"] = "text";
                node["text"] = new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text;
                break;
            case Grid grid:
                node["kind"] = grid.ColumnDefinitions.Count > 1 ? "grid" : "group";
                node["columns"] = string.Join(" ", grid.ColumnDefinitions.Select(column => column.Width.IsStar ? "minmax(0, " + Math.Max(0.1, column.Width.Value).ToString(CultureInfo.InvariantCulture) + "fr)" : column.Width.IsAuto ? "auto" : "minmax(0, " + column.Width.Value.ToString(CultureInfo.InvariantCulture) + "px)"));
                Children(grid.Children.Cast<UIElement>());
                break;
            case StackPanel stack:
                node["kind"] = stack.Orientation == Orientation.Horizontal ? "row" : "group";
                Children(stack.Children.Cast<UIElement>());
                break;
            case Panel panel:
                Children(panel.Children.Cast<UIElement>());
                break;
            case Decorator decorator:
                Children(decorator.Child is { } child ? [child] : []);
                break;
            case ContentControl content:
                if (content.Content is UIElement contentChild) Children([contentChild]);
                else { node["kind"] = "text"; node["text"] = ContentText(content.Content); }
                break;
            default:
                // Composite pages/dialogs are traversed; unsupported leaf visuals are not advertised as migrated.
                Children(LogicalTreeHelper.GetChildren(control).OfType<UIElement>());
                break;
        }
        if (node["kind"]?.GetValue<string>() is "group" or "grid" or "row" && node["children"] is JsonArray { Count: 0 }) return null;
        return node;
    }

    private static void RaiseEnter(UIElement element)
    {
        var source = PresentationSource.FromVisual(element);
        if (source is null) return;
        element.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, Environment.TickCount, System.Windows.Input.Key.Enter)
        {
            RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent
        });
    }

    private static IEnumerable<UIElement> FindButtons(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is MyIconButton or MyButton) yield return (UIElement)child;
            else foreach (var button in FindButtons(child)) yield return button;
        }
    }

    private string ImageData(BitmapSource bitmap) => _images.GetValue(bitmap, source =>
    {
        using var memory = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(memory);
        return new Identity("data:image/png;base64," + Convert.ToBase64String(memory.ToArray()));
    }).Value;

    private static string IconLabel(FrameworkElement control) => control.ToolTip is string tip ? tip :
        TextProperty(control, "SvgIcon").Split('/').LastOrDefault() ?? control.Name;
    private static string TextProperty(object control, string property) => control.GetType().GetProperty(property)?.GetValue(control) as string ?? "";
    private static string ContentText(object? content) => content switch
    {
        TextBlock text => text.Text,
        FlowDocument document => new TextRange(document.ContentStart, document.ContentEnd).Text,
        _ => content?.ToString() ?? ""
    };
    private static string InlineText(InlineCollection inlines, string? fallback) =>
        inlines.FirstInline is { } first && inlines.LastInline is { } last ? new TextRange(first.ContentStart, last.ContentEnd).Text : fallback ?? "";
    private static bool TryText(JsonNode? value, out string text)
    {
        text = "";
        return value is JsonValue json && json.TryGetValue(out text!) && text.Length <= 32768;
    }
    private static bool TryInt(JsonNode? value, out int number)
    {
        number = 0;
        return value is JsonValue json && json.TryGetValue(out number);
    }
    private static bool TryBool(JsonNode? value, out bool flag)
    {
        flag = false;
        return value is JsonValue json && json.TryGetValue(out flag);
    }
}
