using System.Linq.Expressions;

namespace Altruist.Persistence.Postgres;

internal static class ExpressionUtils
{
    public static object? Evaluate(Expression e)
    {
        if (e is ConstantExpression ce)
            return ce.Value;

        return Expression.Lambda(e).Compile().DynamicInvoke();
    }
}
