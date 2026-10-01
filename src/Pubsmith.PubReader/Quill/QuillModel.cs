namespace Pubsmith.PubReader.Quill;

public sealed record QuillChunk(string Name, ushort Id, int Offset, int Length);

public enum ParagraphAlign { Left, Center, Right, Justify }

public enum LineSpacingKind { Multiple, Exact }

/// <param name="Value">Lines for <see cref="LineSpacingKind.Multiple"/>, points for <see cref="LineSpacingKind.Exact"/>.</param>
public readonly record struct LineSpacing(LineSpacingKind Kind, double Value);

/// <param name="Underline">0 none, 1 single, ... (docs/format §4.4).</param>
/// <param name="SuperSub">0 normal, 1 superscript, 2 subscript.</param>
public sealed record CharFormat(string Font, double SizePt, bool Bold, bool Italic, int Underline, RgbColor Color, bool AllCaps, bool SmallCaps, int SuperSub);

public sealed record ParaFormat(ParagraphAlign Alignment, LineSpacing LineSpacing, double SpaceBeforePt, double SpaceAfterPt, double LeftIndentPt, double RightIndentPt, double FirstIndentPt);

public sealed record StoryRun(string Text, CharFormat Format);

public sealed record StoryParagraph(ParaFormat Format, IReadOnlyList<StoryRun> Runs);

public sealed record Story(uint TextId, IReadOnlyList<StoryParagraph> Paragraphs);
