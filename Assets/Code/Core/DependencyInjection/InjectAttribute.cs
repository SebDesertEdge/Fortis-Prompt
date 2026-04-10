using System;

namespace Code.Core.DependencyInjection
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property , AllowMultiple = false)]
    public class InjectAttribute : Attribute  
    {
    }  
}