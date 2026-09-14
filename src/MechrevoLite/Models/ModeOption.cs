namespace MechrevoLite.Models;

/// <summary>运行模式选项（WPF 绑定需要命名属性，不能用 ValueTuple）。</summary>
public class ModeOption
{
    public required string Action { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
}
