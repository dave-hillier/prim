using Mono.Cecil;

namespace Prim.Analysis
{
    /// <summary>
    /// Resolves the types in a member signature against the generic instantiation it is
    /// used through. A call to <c>Dictionary&lt;string, int&gt;::get_Item</c> declares its
    /// return type as <c>!1</c>; at the call site the value is an <c>int</c>.
    /// </summary>
    public static class TypeSubstitution
    {
        /// <summary>
        /// The return type of <paramref name="method"/> at a call site, with the declaring
        /// type's and the method's generic arguments substituted and custom modifiers removed.
        /// </summary>
        public static TypeReference ReturnType(MethodReference method)
        {
            return Substitute(method.ReturnType, method.DeclaringType, method);
        }

        /// <summary>
        /// The type of parameter <paramref name="index"/> of <paramref name="method"/> at a
        /// call site, with generic arguments substituted and custom modifiers removed.
        /// </summary>
        public static TypeReference ParameterType(MethodReference method, int index)
        {
            return Substitute(method.Parameters[index].ParameterType, method.DeclaringType, method);
        }

        /// <summary>
        /// The type of <paramref name="field"/> as seen through its (possibly generic
        /// instance) declaring type, with custom modifiers (e.g. <c>modreq(IsVolatile)</c>)
        /// removed.
        /// </summary>
        public static TypeReference FieldType(FieldReference field)
        {
            return Substitute(field.FieldType, field.DeclaringType, null);
        }

        /// <summary>
        /// Substitutes generic parameters in <paramref name="type"/>: type parameters from
        /// <paramref name="declaringType"/> when it is a generic instance, method parameters
        /// from <paramref name="method"/> when it is a generic method instance. Parameters
        /// with no corresponding argument are left as they are. Custom modifiers are removed.
        /// </summary>
        public static TypeReference Substitute(TypeReference type, TypeReference declaringType, MethodReference method)
        {
            return Substitute(type, declaringType as GenericInstanceType, method as GenericInstanceMethod);
        }

        private static TypeReference Substitute(
            TypeReference type, GenericInstanceType typeArgs, GenericInstanceMethod methodArgs)
        {
            switch (type)
            {
                case null:
                    return null;

                case GenericParameter gp:
                    if (gp.Type == GenericParameterType.Type && typeArgs != null &&
                        gp.Position < typeArgs.GenericArguments.Count)
                    {
                        return typeArgs.GenericArguments[gp.Position];
                    }
                    if (gp.Type == GenericParameterType.Method && methodArgs != null &&
                        gp.Position < methodArgs.GenericArguments.Count)
                    {
                        return methodArgs.GenericArguments[gp.Position];
                    }
                    return gp;

                case RequiredModifierType req:
                    return Substitute(req.ElementType, typeArgs, methodArgs);

                case OptionalModifierType opt:
                    return Substitute(opt.ElementType, typeArgs, methodArgs);

                case ByReferenceType byRef:
                {
                    var element = Substitute(byRef.ElementType, typeArgs, methodArgs);
                    return ReferenceEquals(element, byRef.ElementType) ? byRef : new ByReferenceType(element);
                }

                case PointerType pointer:
                {
                    var element = Substitute(pointer.ElementType, typeArgs, methodArgs);
                    return ReferenceEquals(element, pointer.ElementType) ? pointer : new PointerType(element);
                }

                case PinnedType pinned:
                {
                    var element = Substitute(pinned.ElementType, typeArgs, methodArgs);
                    return ReferenceEquals(element, pinned.ElementType) ? pinned : new PinnedType(element);
                }

                case ArrayType array:
                {
                    var element = Substitute(array.ElementType, typeArgs, methodArgs);
                    if (ReferenceEquals(element, array.ElementType)) return array;
                    if (array.IsVector) return new ArrayType(element);
                    var copy = new ArrayType(element);
                    copy.Dimensions.Clear();
                    foreach (var dimension in array.Dimensions)
                    {
                        copy.Dimensions.Add(dimension);
                    }
                    return copy;
                }

                case GenericInstanceType instance:
                {
                    bool changed = false;
                    var arguments = new TypeReference[instance.GenericArguments.Count];
                    for (int i = 0; i < arguments.Length; i++)
                    {
                        arguments[i] = Substitute(instance.GenericArguments[i], typeArgs, methodArgs);
                        changed |= !ReferenceEquals(arguments[i], instance.GenericArguments[i]);
                    }
                    if (!changed) return instance;
                    var copy = new GenericInstanceType(instance.ElementType);
                    foreach (var argument in arguments)
                    {
                        copy.GenericArguments.Add(argument);
                    }
                    return copy;
                }

                default:
                    return type;
            }
        }

        /// <summary>
        /// The type a method body sees for <c>this</c> (as a value, before taking the
        /// address): its declaring type instantiated over its own generic parameters, e.g.
        /// <c>G&lt;T&gt;</c> rather than the open definition <c>G`1</c>, which is not a
        /// valid type in a signature.
        /// </summary>
        public static TypeReference SelfType(TypeDefinition type)
        {
            if (!type.HasGenericParameters) return type;
            var self = new GenericInstanceType(type);
            foreach (var gp in type.GenericParameters)
            {
                self.GenericArguments.Add(gp);
            }
            return self;
        }
    }
}
