namespace Prim.Core
{
    /// <summary>
    /// The kind of slot in a frame.
    /// </summary>
    public enum SlotKind
    {
        /// <summary>
        /// A local variable declared in the method.
        /// </summary>
        Local,

        /// <summary>
        /// A method argument/parameter.
        /// </summary>
        Argument,

        /// <summary>
        /// A value on the evaluation stack at the yield point.
        /// </summary>
        EvalStack
    }
}
