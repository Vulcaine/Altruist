using System.Linq.Expressions;

namespace Altruist.Persistence.Postgres;

/// <summary>Expression helpers shared by the Postgres translators.</summary>
internal static class ExpressionUtils
{
    /// <summary>
    /// Evaluates a parameter-free expression (constant, captured variable, computed value) client-side by
    /// compiling it. Throws when <paramref name="expr"/> references a lambda parameter. Compiles a delegate per
    /// call for non-constants, so it is not free.
    /// </summary>
    /// <param name="expr">The value expression.</param>
    /// <returns>The value, boxed.</returns>
    public static object? Evaluate(Expression expr)
    {
        if (expr is ConstantExpression c)
            return c.Value;

        var lambda = Expression.Lambda<Func<object?>>(
            Expression.Convert(expr, typeof(object)));

        return lambda.Compile().Invoke();
    }
}
