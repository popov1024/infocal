namespace Infocal.Calendar.Models;

/// <summary>
/// Event type (subcategory) reference entity. Slug is the primary key.
/// E.g. "wow-quiz" → "ВАУ КВИЗ", "ice-palace" → "Ледовый дворец"
/// </summary>
public class TypeEntity
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
}
