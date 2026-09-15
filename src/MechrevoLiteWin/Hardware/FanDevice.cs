namespace MechrevoLite;
/// <summary>
/// 风扇设备枚举。
///
/// 机械革命只有 CPU 与 GPU 两颗可读可控的风扇。<see cref="Mid"/> 保留枚举值供
/// 继承自 g-helper 的界面编译，但恒不可用——这套协议里没有第三颗风扇的读数
/// （详见 docs/hardware/README.md）。
///
/// 原来还有一个 XGM = 3（XG Mobile 外置显卡坞的风扇），已随该族一起删除。
/// </summary>
public enum AsusFan
{
    CPU = 0,
    GPU = 1,
    Mid = 2
}
/// <summary>模式枚举（G-Helper UI 使用）。Mechrevo 映射：Silent→办公、Balanced→游戏、Turbo→极速。</summary>
