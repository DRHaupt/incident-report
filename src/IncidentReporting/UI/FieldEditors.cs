using System.Globalization;
using IncidentReporting.Models;

namespace IncidentReporting.UI;

/// <summary>Binds a FieldDefinition to a WinForms control and converts to/from the stored string value.</summary>
public interface IFieldEditor
{
    FieldDefinition Field { get; }
    Control Control { get; }
    string Value { get; set; }
    event EventHandler? Changed;
}

public static class FieldEditorFactory
{
    public const int EditorWidth = 420;

    public static IFieldEditor Create(FieldDefinition field) => field.Type switch
    {
        FieldType.MultiLine => new TextEditor(field, multiline: true),
        FieldType.Number => new TextEditor(field, numeric: true),
        FieldType.Date => new DateEditor(field, FieldFormats.Date),
        FieldType.Time => new DateEditor(field, FieldFormats.Time),
        FieldType.DateTime => new DateEditor(field, FieldFormats.DateTime),
        FieldType.Choice => new ChoiceEditor(field, field.Options),
        FieldType.YesNo => new ChoiceEditor(field, new[] { "Yes", "No", "Unknown" }),
        FieldType.MultiChoice => new MultiChoiceEditor(field),
        FieldType.CheckBox => new CheckEditor(field),
        _ => new TextEditor(field)
    };

    private sealed class TextEditor : IFieldEditor
    {
        private readonly TextBox _box;
        public FieldDefinition Field { get; }
        public Control Control => _box;
        public event EventHandler? Changed;

        public TextEditor(FieldDefinition field, bool multiline = false, bool numeric = false)
        {
            Field = field;
            _box = new TextBox { Width = numeric ? 140 : EditorWidth, MaxLength = multiline ? 32000 : 500 };
            if (multiline)
            {
                _box.Multiline = true;
                _box.AcceptsReturn = true;
                _box.ScrollBars = ScrollBars.Vertical;
                _box.Height = 78;
                _box.Width = EditorWidth + 120;
            }
            if (numeric)
            {
                _box.TextAlign = HorizontalAlignment.Right;
                _box.KeyPress += (_, e) =>
                {
                    var sep = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
                    if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar) && e.KeyChar != '-' && e.KeyChar.ToString() != sep)
                        e.Handled = true;
                };
            }
            if (field.Type == FieldType.Signature)
            {
                _box.Font = new Font("Segoe UI", 10f, FontStyle.Italic);
                _box.Width = 300;
            }
            if (field.Type == FieldType.Phone)
                _box.Width = 200;
            _box.TextChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        }

        public string Value
        {
            get => _box.Text.Trim();
            set => _box.Text = value;
        }
    }

    private sealed class DateEditor : IFieldEditor
    {
        private readonly DateTimePicker _picker;
        private readonly string _format;
        public FieldDefinition Field { get; }
        public Control Control => _picker;
        public event EventHandler? Changed;

        public DateEditor(FieldDefinition field, string format)
        {
            Field = field;
            _format = format;
            _picker = new DateTimePicker { ShowCheckBox = true, Checked = false };
            var dtf = CultureInfo.CurrentCulture.DateTimeFormat;
            switch (format)
            {
                case FieldFormats.Time:
                    _picker.Format = DateTimePickerFormat.Custom;
                    _picker.CustomFormat = dtf.ShortTimePattern;
                    _picker.ShowUpDown = true;
                    _picker.Width = 130;
                    break;
                case FieldFormats.DateTime:
                    _picker.Format = DateTimePickerFormat.Custom;
                    _picker.CustomFormat = dtf.ShortDatePattern + "  " + dtf.ShortTimePattern;
                    _picker.Width = 200;
                    break;
                default:
                    _picker.Format = DateTimePickerFormat.Short;
                    _picker.Width = 140;
                    break;
            }
            _picker.ValueChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        }

        public string Value
        {
            get => _picker.Checked ? _picker.Value.ToString(_format, CultureInfo.InvariantCulture) : "";
            set
            {
                if (DateTime.TryParseExact(value, _format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                {
                    _picker.Value = _format == FieldFormats.Time ? DateTime.Today.Add(d.TimeOfDay) : d;
                    _picker.Checked = true;
                }
                else
                {
                    _picker.Value = DateTime.Now;
                    _picker.Checked = false;
                }
            }
        }
    }

    private sealed class ChoiceEditor : IFieldEditor
    {
        private readonly ComboBox _combo;
        public FieldDefinition Field { get; }
        public Control Control => _combo;
        public event EventHandler? Changed;

        public ChoiceEditor(FieldDefinition field, IReadOnlyList<string> options)
        {
            Field = field;
            _combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, IntegralHeight = false, MaxDropDownItems = 16 };
            _combo.Items.Add("");
            foreach (var o in options)
                _combo.Items.Add(o);
            var widest = options.Count == 0 ? 0 : options.Max(o => TextRenderer.MeasureText(o, _combo.Font).Width);
            _combo.Width = Math.Clamp(widest + 40, 140, EditorWidth);
            _combo.SelectedIndexChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        }

        public string Value
        {
            get => _combo.SelectedItem as string ?? "";
            set
            {
                var idx = _combo.Items.IndexOf(value ?? "");
                if (idx < 0 && !string.IsNullOrEmpty(value))
                    idx = _combo.Items.Add(value); // preserve values from older template versions
                _combo.SelectedIndex = Math.Max(idx, 0);
            }
        }
    }

    private sealed class MultiChoiceEditor : IFieldEditor
    {
        private readonly FlowLayoutPanel _panel;
        private readonly List<CheckBox> _boxes = new();
        public FieldDefinition Field { get; }
        public Control Control => _panel;
        public event EventHandler? Changed;

        public MultiChoiceEditor(FieldDefinition field)
        {
            Field = field;
            _panel = new FlowLayoutPanel
            {
                Width = EditorWidth + 160,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MaximumSize = new Size(EditorWidth + 160, 0),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(2),
                WrapContents = true
            };
            foreach (var option in field.Options)
            {
                var cb = new CheckBox { Text = option, Width = 186, AutoEllipsis = true, Margin = new Padding(3, 1, 3, 1) };
                cb.CheckedChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
                _boxes.Add(cb);
                _panel.Controls.Add(cb);
            }
        }

        public string Value
        {
            get => string.Join(FieldFormats.ListSeparator, _boxes.Where(b => b.Checked).Select(b => b.Text));
            set
            {
                var selected = (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var b in _boxes)
                    b.Checked = selected.Contains(b.Text);
            }
        }
    }

    private sealed class CheckEditor : IFieldEditor
    {
        private readonly CheckBox _box;
        public FieldDefinition Field { get; }
        public Control Control => _box;
        public event EventHandler? Changed;

        public CheckEditor(FieldDefinition field)
        {
            Field = field;
            _box = new CheckBox { Text = "", AutoSize = true };
            _box.CheckedChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        }

        public string Value
        {
            get => _box.Checked ? "Yes" : "";
            set => _box.Checked = value == "Yes";
        }
    }
}
