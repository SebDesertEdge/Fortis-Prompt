namespace Fortis.Core.DependencyInjection
{
    public static class Utilities
    {
        public static void ResolveDependencies(this object obj, bool throwException = false)
        {
            DiContainer.ResolveDependencies(obj, throwException);
        }
    }
}