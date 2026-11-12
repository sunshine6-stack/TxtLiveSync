// .NET Framework 4.8 的运行时里没有这个标记类型，但 C# 编译器生成 record / init 访问器时要用到，
// 自己补一份即可。net8 里已经内置，重复声明会同名类型冲突，所以只在 net48 目标下编译。
#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
