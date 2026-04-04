using System.Linq.Expressions;

namespace Altruist.Persistence.Postgres;

internal static class ExpressionUtils
{
    public static object? Evaluate(Expression expr)
    {
        if (expr is ConstantExpression c)
            return c.Value;

        var lambda = Expression.Lambda<Func<object?>>(
            Expression.Convert(expr, typeof(object)));

        return lambda.Compile().Invoke();
    }
}
