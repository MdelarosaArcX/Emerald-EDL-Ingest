using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Emerald.Core;

/// <summary>
/// Turns a TextBox into a fixed HH:MM:SS:FF field.
///
/// The shape never changes. The box always holds eleven characters — eight digits and
/// three colons — from the moment it is attached, so an operator is never looking at a
/// half-formed timecode and never has to type the zeros that are already there. Typing
/// <b>overwrites</b> the digit under the caret and moves on; backspace and delete put a
/// zero back rather than closing the gap. To turn 00:00:00:00 into 00:01:00:00 you put the
/// caret on the one digit that is wrong and type 1.
///
/// That is what a broadcast timecode field does, and it is the reason this is a mask rather
/// than validation: the field cannot be got into a state that is not a timecode, so what an
/// operator sees is always what will be parsed. Whether the value is a <i>legal</i> timecode
/// — minutes under 60, frames under the rate — is still the caller's to check and report,
/// because silently correcting a number somebody typed is not something a broadcast
/// application should do.
///
/// There is deliberately no empty state. A field that means "no limit" expresses it as
/// 00:00:00:00, which the caller reads as it likes.
/// </summary>
public static class TimecodeMask
{
    private const int DigitCount = 8;

    /// <summary>Where each digit sits in "HH:MM:SS:FF"; the ninth entry is the end of the text.</summary>
    private static readonly int[] DigitPositions = { 0, 1, 3, 4, 6, 7, 9, 10, 11 };

    /// <summary>
    /// Boxes this class is currently writing to. Every write raises TextChanged, which is
    /// also where a programmatic value gets normalised — without this the two would chase
    /// each other.
    /// </summary>
    private static readonly HashSet<TextBox> Writing = new();

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(TimecodeMask),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);

    /// <summary>The canonical form of any text: eight digits, padded and colon-separated.</summary>
    public static string Canonical(string? text)
    {
        string digits = OnlyDigits(text);

        if (digits.Length > DigitCount) digits = digits[..DigitCount];

        return Format(digits.PadRight(DigitCount, '0'));
    }

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;

        box.PreviewTextInput -= OnPreviewTextInput;
        box.PreviewKeyDown -= OnPreviewKeyDown;
        box.TextChanged -= OnTextChanged;
        box.GotKeyboardFocus -= OnGotKeyboardFocus;
        box.PreviewMouseLeftButtonUp -= OnMouseUp;
        DataObject.RemovePastingHandler(box, OnPaste);

        if (!(bool)e.NewValue) return;

        box.PreviewTextInput += OnPreviewTextInput;
        box.PreviewKeyDown += OnPreviewKeyDown;
        box.TextChanged += OnTextChanged;
        box.GotKeyboardFocus += OnGotKeyboardFocus;
        box.PreviewMouseLeftButtonUp += OnMouseUp;
        DataObject.AddPastingHandler(box, OnPaste);

        // Attached onto whatever the box already held, so the field is in shape before it is
        // ever shown - including the empty string a freshly built TextBox starts with.
        Write(box, Canonical(box.Text), 0);
    }

    // ------------------------------------------------------------------ input

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Everything is handled here, so a stray character never reaches the field.
        e.Handled = true;

        if (sender is TextBox box && OnlyDigits(e.Text) is { Length: > 0 } typed)
            Type(box, typed);
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;

        switch (e.Key)
        {
            case Key.Back:
                Erase(box, forward: false);
                e.Handled = true;
                break;

            case Key.Delete:
                Erase(box, forward: true);
                e.Handled = true;
                break;

            case Key.Space:
                e.Handled = true;
                break;
        }
    }

    private static void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();

        if (sender is not TextBox box) return;
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted) return;

        if (OnlyDigits(pasted) is { Length: > 0 } digits) Type(box, digits);
    }

    /// <summary>Tabbing in lands on the first digit, so eight keystrokes replace the field.</summary>
    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && box.SelectionLength == 0) Place(box, 0);
    }

    /// <summary>A click that lands on a colon belongs to the digit after it.</summary>
    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox box && box.SelectionLength == 0)
            Place(box, DigitIndexAt(box.CaretIndex));
    }

    /// <summary>
    /// Puts a value set in code — a derived EOM, a restored setting — into the field's shape.
    /// </summary>
    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box || Writing.Contains(box)) return;

        string canonical = Canonical(box.Text);
        if (canonical != box.Text) Write(box, canonical, DigitIndexAt(box.CaretIndex));
    }

    // ------------------------------------------------------------------ edits

    private static void Type(TextBox box, string incoming)
    {
        char[] digits = Digits(box);
        int at = DigitIndexAt(box.SelectionStart);

        foreach (char c in incoming)
        {
            if (at >= DigitCount) break;
            digits[at++] = c;
        }

        Write(box, Format(new string(digits)), at);
    }

    private static void Erase(TextBox box, bool forward)
    {
        char[] digits = Digits(box);
        int start = DigitIndexAt(box.SelectionStart);

        if (box.SelectionLength > 0)
        {
            // A selection is zeroed where it stands. The field cannot get shorter, so there
            // is nothing to close up.
            int end = DigitIndexAt(box.SelectionStart + box.SelectionLength);
            for (int i = start; i < end && i < DigitCount; i++) digits[i] = '0';

            Write(box, Format(new string(digits)), start);
            return;
        }

        if (forward)
        {
            if (start >= DigitCount) return;
            digits[start] = '0';
            Write(box, Format(new string(digits)), start);
        }
        else
        {
            if (start == 0) return;
            digits[start - 1] = '0';
            Write(box, Format(new string(digits)), start - 1);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static char[] Digits(TextBox box) =>
        OnlyDigits(box.Text).PadRight(DigitCount, '0')[..DigitCount].ToCharArray();

    private static void Write(TextBox box, string text, int caretDigit)
    {
        Writing.Add(box);

        try
        {
            if (box.Text != text) box.Text = text;
            Place(box, caretDigit);
        }
        finally
        {
            Writing.Remove(box);
        }
    }

    private static void Place(TextBox box, int digitIndex)
    {
        box.CaretIndex = DigitPositions[Math.Clamp(digitIndex, 0, DigitCount)];
    }

    private static string OnlyDigits(string? text) =>
        text is null ? "" : new string(text.Where(char.IsAsciiDigit).ToArray());

    /// <summary>How many digits sit to the left of a caret position in the formatted text.</summary>
    private static int DigitIndexAt(int caretIndex)
    {
        for (int i = 0; i <= DigitCount; i++)
            if (DigitPositions[i] >= caretIndex) return i;

        return DigitCount;
    }

    /// <summary>Inserts a colon before every second digit: "10000000" becomes "10:00:00:00".</summary>
    private static string Format(string digits)
    {
        var sb = new StringBuilder(digits.Length + 3);

        for (int i = 0; i < digits.Length; i++)
        {
            if (i > 0 && i % 2 == 0) sb.Append(':');
            sb.Append(digits[i]);
        }

        return sb.ToString();
    }
}
