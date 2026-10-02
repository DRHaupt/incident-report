using IncidentReporting.Models;

namespace IncidentReporting.Templates;

/// <summary>Fluent helper used by the catalog to declare form templates compactly.</summary>
internal sealed class FormBuilder
{
    private readonly FormDefinition _form;
    private SectionDefinition? _section;

    public FormBuilder(string code, string name, string category, string description)
    {
        _form = new FormDefinition { Code = code, Name = name, Category = category, Description = description };
    }

    public FormBuilder Section(string title)
    {
        _section = new SectionDefinition { Title = title };
        _form.Sections.Add(_section);
        return this;
    }

    public FormBuilder Field(string key, string label, FieldType type, bool required = false, string? hint = null, params string[] options)
    {
        if (_section is null)
            throw new InvalidOperationException("Call Section() before adding fields.");
        if (_form.AllFields.Any(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Duplicate field key '{key}' in form {_form.Code}.");
        _section.Fields.Add(new FieldDefinition
        {
            Key = key, Label = label, Type = type, Required = required, Hint = hint, Options = options
        });
        return this;
    }

    public FormBuilder Text(string key, string label, bool required = false, string? hint = null) =>
        Field(key, label, FieldType.Text, required, hint);

    public FormBuilder Memo(string key, string label, bool required = false, string? hint = null) =>
        Field(key, label, FieldType.MultiLine, required, hint);

    public FormBuilder Number(string key, string label, bool required = false, string? hint = null) =>
        Field(key, label, FieldType.Number, required, hint);

    public FormBuilder Date(string key, string label, bool required = false) =>
        Field(key, label, FieldType.Date, required);

    public FormBuilder Time(string key, string label, bool required = false) =>
        Field(key, label, FieldType.Time, required);

    public FormBuilder DateTime(string key, string label, bool required = false) =>
        Field(key, label, FieldType.DateTime, required);

    public FormBuilder Choice(string key, string label, bool required, params string[] options) =>
        Field(key, label, FieldType.Choice, required, null, options);

    public FormBuilder Choice(string key, string label, params string[] options) =>
        Field(key, label, FieldType.Choice, false, null, options);

    public FormBuilder Multi(string key, string label, params string[] options) =>
        Field(key, label, FieldType.MultiChoice, false, null, options);

    public FormBuilder YesNo(string key, string label, bool required = false) =>
        Field(key, label, FieldType.YesNo, required);

    public FormBuilder Check(string key, string label) =>
        Field(key, label, FieldType.CheckBox);

    public FormBuilder Phone(string key, string label, bool required = false) =>
        Field(key, label, FieldType.Phone, required);

    public FormBuilder Email(string key, string label, bool required = false) =>
        Field(key, label, FieldType.Email, required);

    public FormBuilder Signature(string key, string label, bool required = false) =>
        Field(key, label, FieldType.Signature, required);

    /// <summary>Applies a reusable block of sections (e.g. the standard header).</summary>
    public FormBuilder Include(Action<FormBuilder> block)
    {
        block(this);
        return this;
    }

    public FormDefinition Build() => _form;
}
