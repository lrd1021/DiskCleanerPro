using System.Runtime.CompilerServices;

// 允许冒烟测试工程（DiskCleaner.SmokeTest）反射测试本程序集的内部守卫方法，
// 以便在无头环境中覆盖 Elevated helper 的安全逻辑（R16）。
[assembly: InternalsVisibleTo("DiskCleaner.SmokeTest")]
