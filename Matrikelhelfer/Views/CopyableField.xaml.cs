using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MahApps.Metro.IconPacks;

namespace Matrikelhelfer.Views;

// A labeled read-only value with a button that copies it to the clipboard -
// used to build up the citation display from individually-copyable fields
// instead of one big text block, so the user can paste exactly the piece
// their genealogy software's field wants. Optionally also shows a second,
// generic "extra action" button (save image to disk, open URL in browser,
// ...) bound to a command/icon/tooltip supplied by the ViewModel, since the
// action itself is business logic that doesn't belong in this control's
// code-behind - only the copy-to-clipboard behavior is simple enough to
// live here directly.
public partial class CopyableField : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(CopyableField));

    // Optional leading icon (left of the floating-watermark label). Default
    // None -> the icon collapses and the field lays out exactly as before.
    public static readonly DependencyProperty IconKindProperty =
        DependencyProperty.Register(nameof(IconKind), typeof(PackIconFontAwesome6Kind), typeof(CopyableField),
            new PropertyMetadata(PackIconFontAwesome6Kind.None));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(CopyableField),
            new PropertyMetadata(OnWarnStateChanged));

    public static readonly DependencyProperty ShowExtraButtonProperty =
        DependencyProperty.Register(nameof(ShowExtraButton), typeof(bool), typeof(CopyableField));

    public static readonly DependencyProperty ExtraCommandProperty =
        DependencyProperty.Register(nameof(ExtraCommand), typeof(ICommand), typeof(CopyableField));

    public static readonly DependencyProperty ExtraIconKindProperty =
        DependencyProperty.Register(nameof(ExtraIconKind), typeof(PackIconFontAwesome6Kind), typeof(CopyableField));

    public static readonly DependencyProperty ExtraTooltipProperty =
        DependencyProperty.Register(nameof(ExtraTooltip), typeof(string), typeof(CopyableField));

    // Opt-in wrapping for long rendered text (Quellen-/Zitatangabe). Off by
    // default because wrapping would break the Links fields' URLs mid-word.
    public static readonly DependencyProperty ValueWrappingProperty =
        DependencyProperty.Register(nameof(ValueWrapping), typeof(TextWrapping), typeof(CopyableField),
            new PropertyMetadata(TextWrapping.NoWrap));

    // Opt-in editability (e.g. the Seite field, whose parsed value rarely
    // matches the real handwritten page number). Fields are read-only by
    // default; an editable one needs its Value binding set to Mode=TwoWay.
    public static readonly DependencyProperty IsValueReadOnlyProperty =
        DependencyProperty.Register(nameof(IsValueReadOnly), typeof(bool), typeof(CopyableField),
            new PropertyMetadata(true));

    // Opt-in "this field still needs a value" indicator: an empty field
    // gets MahApps' red validation border (visual only - real binding
    // validation can't reach through the nested Value DP indirection).
    public static readonly DependencyProperty WarnWhenEmptyProperty =
        DependencyProperty.Register(nameof(WarnWhenEmpty), typeof(bool), typeof(CopyableField),
            new PropertyMetadata(false, OnWarnStateChanged));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public PackIconFontAwesome6Kind IconKind
    {
        get => (PackIconFontAwesome6Kind)GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool ShowExtraButton
    {
        get => (bool)GetValue(ShowExtraButtonProperty);
        set => SetValue(ShowExtraButtonProperty, value);
    }

    public ICommand? ExtraCommand
    {
        get => (ICommand?)GetValue(ExtraCommandProperty);
        set => SetValue(ExtraCommandProperty, value);
    }

    public PackIconFontAwesome6Kind ExtraIconKind
    {
        get => (PackIconFontAwesome6Kind)GetValue(ExtraIconKindProperty);
        set => SetValue(ExtraIconKindProperty, value);
    }

    public string ExtraTooltip
    {
        get => (string)GetValue(ExtraTooltipProperty);
        set => SetValue(ExtraTooltipProperty, value);
    }

    public TextWrapping ValueWrapping
    {
        get => (TextWrapping)GetValue(ValueWrappingProperty);
        set => SetValue(ValueWrappingProperty, value);
    }

    public bool IsValueReadOnly
    {
        get => (bool)GetValue(IsValueReadOnlyProperty);
        set => SetValue(IsValueReadOnlyProperty, value);
    }

    public bool WarnWhenEmpty
    {
        get => (bool)GetValue(WarnWhenEmptyProperty);
        set => SetValue(WarnWhenEmptyProperty, value);
    }

    public CopyableField()
    {
        InitializeComponent();
        UpdateWarnBorder();
    }

    static void OnWarnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CopyableField)d).UpdateWarnBorder();

    // Applies/clears the WarnWhenEmpty red border as a LOCAL BorderBrush
    // value on the TextBox - local values outrank the MahApps style and
    // template triggers, which silently override a mere style-trigger
    // setter (and, as a bonus, the red border survives hover/focus).
    void UpdateWarnBorder()
    {
        if (ValueBox == null)
        {
            // DP callbacks can fire before InitializeComponent.
            return;
        }
        if (WarnWhenEmpty && string.IsNullOrEmpty(Value))
        {
            ValueBox.BorderBrush =
                TryFindResource("MahApps.Brushes.Controls.Validation") as Brush ?? Brushes.Firebrick;
        }
        else
        {
            ValueBox.ClearValue(BorderBrushProperty);
        }
    }

    void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(Value))
        {
            Clipboard.SetText(Value);
        }
    }
}
