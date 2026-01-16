using System;

namespace Prim.Core
{
    /// <summary>
    /// Marks a method or class for continuation transformation.
    /// Methods with this attribute will have yield points injected,
    /// and state capture/restore code generated.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ContinuableAttribute : Attribute
    {
        /// <summary>
        /// Creates a ContinuableAttribute with default settings.
        /// </summary>
        public ContinuableAttribute()
        {
        }
    }

    /// <summary>
    /// Marks a method as an explicit yield point.
    /// When called, checks if the host has requested a yield.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class YieldPointAttribute : Attribute
    {
    }
}
