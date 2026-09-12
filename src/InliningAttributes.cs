namespace RNGTick;

/// <summary>
/// Marks a small static method whose body the generator may paste into a caller.
///
/// <para>The method stays a perfectly ordinary method: it compiles, the tests call it, and
/// nothing about it changes. The attribute only grants permission for a copy of its body to
/// appear somewhere else, which is why the arithmetic can be written once here and still run
/// flat in the postfix.</para>
///
/// <para>Supported shape is narrow on purpose: a single trailing <c>return</c>, or an expression
/// body. Anything else is a build error from RNGTICK001 rather than something subtly wrong at
/// runtime.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class InlinableAttribute : Attribute;

/// <summary>
/// Marks the method the generator flattens into <c>RNGPatches.AfterRoll</c>.
///
/// <para>Exactly one method carries this. Its first parameter becomes the incoming roll and its
/// second the tick; every call it makes to an <see cref="InlinableAttribute"/> method is replaced
/// by that method's body, repeatedly, until the postfix contains no calls at all.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class InlineIntoPostfixAttribute : Attribute;
