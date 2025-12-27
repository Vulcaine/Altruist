using System.Linq.Expressions;
using System.Reflection;

using Altruist.UORM;

namespace Altruist.Persistence;

internal sealed class PgPrefabWhereTranslator
{
    private readonly PrefabMeta _prefab;

    public PgPrefabWhereTranslator(PrefabMeta prefab)
    {
        _prefab = prefab;
    }

    public SqlFragment Translate(LambdaExpression lambda)
    {
        if (lambda.Parameters.Count != 1)
            throw new NotSupportedException("Prefab Where must have exactly one parameter.");

        return Visit(lambda.Body);
    }

    private SqlFragment Visit(Expression expr)
    {
        return expr switch
        {
            UnaryExpression u when u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
                => Visit(u.Operand),

            BinaryExpression b when b.NodeType == ExpressionType.AndAlso
                => Combine("AND", Visit(b.Left), Visit(b.Right)),

            BinaryExpression b when b.NodeType == ExpressionType.OrElse
                => Combine("OR", Visit(b.Left), Visit(b.Right)),

            BinaryExpression b when b.NodeType is ExpressionType.Equal or ExpressionType.NotEqual
                => VisitComparison(b),

            MethodCallExpression m
                => VisitMethodCall(m),

            _ => throw new NotSupportedException($"Unsupported expression in prefab WHERE: {expr.NodeType} ({expr})")
        };
    }

    private SqlFragment VisitMethodCall(MethodCallExpression m)
    {
        // Support: p.Equipment.Any(e => e.Kind == "Sword")
        if (IsEnumerableAny(m, out var sourceExpr, out var predicate))
        {
            var compName = GetComponentNameFromAnySource(sourceExpr);
            if (!_prefab.ComponentsByName.TryGetValue(compName, out var comp))
                throw new InvalidOperationException($"Unknown prefab component '{compName}'.");

            if (comp.Kind != PrefabComponentKind.Collection)
                throw new NotSupportedException($"Any() is only supported on collection components. '{compName}' is {comp.Kind}.");

            // EXISTS (SELECT 1 FROM dep d WHERE d.fk = r.storage_id AND <predicate>)
            var depDoc = VaultDocument.From(comp.ComponentType);
            var depTable = QualifiedTable(comp.ComponentType, depDoc);

            var fkCol = Col(depDoc, comp.ForeignKeyPropertyName);
            var rootPkCol = Col(VaultDocument.From(_prefab.RootComponentType), nameof(IVaultModel.StorageId));

            var sql = $"EXISTS (SELECT 1 FROM {depTable} d WHERE d.{Q(fkCol)} = r.{Q(rootPkCol)}";
            var args = new List<object?>();

            if (predicate != null)
            {
                // predicate param is the dependent row parameter
                var predFrag = VisitDependentPredicate(depDoc, predicate);
                sql += $" AND ({predFrag.Sql})";
                args.AddRange(predFrag.Parameters);
            }

            sql += ")";

            return new SqlFragment(sql, args);
        }

        throw new NotSupportedException($"Unsupported method call in prefab WHERE: {m.Method.Name}");
    }

    private SqlFragment VisitComparison(BinaryExpression b)
    {
        // Member == Constant
        if (TryResolveComponentMember(b.Left, out var comp, out var memberLogical, rowAlias: out var alias) &&
            TryEvaluate(b.Right, out var value))
        {
            return BuildComparison(comp, memberLogical, alias, b.NodeType, value);
        }

        if (TryResolveComponentMember(b.Right, out var comp2, out var memberLogical2, rowAlias: out var alias2) &&
            TryEvaluate(b.Left, out var value2))
        {
            // symmetric for ==/!=
            return BuildComparison(comp2, memberLogical2, alias2, b.NodeType, value2);
        }

        throw new NotSupportedException("Prefab WHERE comparisons must be component member compared to a value.");
    }

    private SqlFragment BuildComparison(
        PrefabComponentMeta comp,
        string memberLogical,
        string alias,
        ExpressionType op,
        object? value)
    {
        var isEq = op == ExpressionType.Equal;
        var isNe = op == ExpressionType.NotEqual;

        if (!isEq && !isNe)
            throw new NotSupportedException("Only == and != are supported.");

        // Root member: r."<col>" = ?
        if (comp.Kind == PrefabComponentKind.Root)
        {
            var rootDoc = VaultDocument.From(_prefab.RootComponentType);
            var col = Col(rootDoc, memberLogical);

            if (value is null)
                return new SqlFragment(isEq ? $"{alias}.{Q(col)} IS NULL" : $"{alias}.{Q(col)} IS NOT NULL");

            return new SqlFragment($"{alias}.{Q(col)} {(isEq ? "=" : "!=")} ?", new List<object?> { value });
        }

        // Single ref member: EXISTS join
        if (comp.Kind == PrefabComponentKind.Single)
        {
            // root FK (on root) -> dependent PK
            var rootDoc = VaultDocument.From(_prefab.RootComponentType);
            var rootFkCol = Col(rootDoc, comp.ForeignKeyPropertyName);

            var depDoc = VaultDocument.From(comp.ComponentType);
            var depTable = QualifiedTable(comp.ComponentType, depDoc);

            var depPkCol = Col(depDoc, comp.PrincipalKeyPropertyName);
            var depMemberCol = Col(depDoc, memberLogical);

            var cmp = value is null
                ? (isEq ? $"c.{Q(depMemberCol)} IS NULL" : $"c.{Q(depMemberCol)} IS NOT NULL")
                : $"c.{Q(depMemberCol)} {(isEq ? "=" : "!=")} ?";

            var sql = $"EXISTS (SELECT 1 FROM {depTable} c WHERE c.{Q(depPkCol)} = r.{Q(rootFkCol)} AND {cmp})";

            var args = new List<object?>();
            if (value is not null)
                args.Add(value);

            return new SqlFragment(sql, args);
        }

        // Collection direct member compare is not supported outside Any()
        throw new NotSupportedException($"Direct comparison against collection component '{comp.Name}' is not supported. Use Any(...).");
    }

    private SqlFragment VisitDependentPredicate(VaultDocument depDoc, LambdaExpression predicate)
    {
        // Supports simple comparisons + AND/OR on dependent row parameter.
        return VisitDependentExpr(depDoc, predicate.Body);
    }

    private SqlFragment VisitDependentExpr(VaultDocument depDoc, Expression expr)
    {
        return expr switch
        {
            UnaryExpression u when u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
                => VisitDependentExpr(depDoc, u.Operand),

            BinaryExpression b when b.NodeType == ExpressionType.AndAlso
                => Combine("AND", VisitDependentExpr(depDoc, b.Left), VisitDependentExpr(depDoc, b.Right)),

            BinaryExpression b when b.NodeType == ExpressionType.OrElse
                => Combine("OR", VisitDependentExpr(depDoc, b.Left), VisitDependentExpr(depDoc, b.Right)),

            BinaryExpression b when b.NodeType is ExpressionType.Equal or ExpressionType.NotEqual
                => VisitDependentComparison(depDoc, b),

            _ => throw new NotSupportedException($"Unsupported expression inside Any(): {expr.NodeType} ({expr})")
        };
    }

    private SqlFragment VisitDependentComparison(VaultDocument depDoc, BinaryExpression b)
    {
        if (b.Left is MemberExpression me && TryEvaluate(b.Right, out var value))
        {
            var col = Col(depDoc, me.Member.Name);
            return DepCmp(col, b.NodeType, value);
        }

        if (b.Right is MemberExpression me2 && TryEvaluate(b.Left, out var value2))
        {
            var col = Col(depDoc, me2.Member.Name);
            return DepCmp(col, b.NodeType, value2);
        }

        throw new NotSupportedException("Any() predicate must compare a dependent member to a value.");
    }

    private static SqlFragment DepCmp(string physicalCol, ExpressionType op, object? value)
    {
        var isEq = op == ExpressionType.Equal;
        var isNe = op == ExpressionType.NotEqual;

        if (value is null)
            return new SqlFragment(isEq ? $"d.{Q(physicalCol)} IS NULL" : $"d.{Q(physicalCol)} IS NOT NULL");

        return new SqlFragment($"d.{Q(physicalCol)} {(isEq ? "=" : "!=")} ?", new List<object?> { value });
    }

    private bool TryResolveComponentMember(Expression expr, out PrefabComponentMeta comp, out string memberLogical, out string rowAlias)
    {
        comp = default!;
        memberLogical = "";
        rowAlias = "r"; // root alias default

        while (expr is UnaryExpression u &&
               (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
            expr = u.Operand;

        // expecting: p.<Component>.<Member>
        if (expr is not MemberExpression leaf)
            return false;

        if (leaf.Expression is not MemberExpression owner)
            return false;

        var componentName = owner.Member.Name;

        if (!_prefab.ComponentsByName.TryGetValue(componentName, out comp!))
            return false;

        // Root alias is "r" in our root FROM
        rowAlias = "r";
        memberLogical = leaf.Member.Name;
        return true;
    }

    private static bool IsEnumerableAny(MethodCallExpression m, out Expression source, out LambdaExpression? predicate)
    {
        predicate = null;
        source = null!;

        if (m.Method.Name != "Any")
            return false;

        // Enumerable.Any(source) or Enumerable.Any(source, predicate)
        if (m.Arguments.Count == 1)
        {
            source = m.Arguments[0];
            return true;
        }

        if (m.Arguments.Count == 2)
        {
            source = m.Arguments[0];
            predicate = StripQuote(m.Arguments[1]) as LambdaExpression
                ?? throw new NotSupportedException("Any() predicate must be a lambda.");
            return true;
        }

        return false;
    }

    private static Expression StripQuote(Expression e)
    {
        while (e.NodeType == ExpressionType.Quote && e is UnaryExpression u)
            e = u.Operand;
        return e;
    }

    private static string GetComponentNameFromAnySource(Expression sourceExpr)
    {
        // sourceExpr should be MemberExpression: p.Equipment
        while (sourceExpr is UnaryExpression u &&
               (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
            sourceExpr = u.Operand;

        if (sourceExpr is MemberExpression me)
            return me.Member.Name;

        throw new NotSupportedException("Any() source must be a prefab component member, e.g. p => p.Equipment.Any(...)");
    }

    private static bool TryEvaluate(Expression expr, out object? value)
    {
        try
        {
            value = Expression.Lambda(expr).Compile().DynamicInvoke();
            return true;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static SqlFragment Combine(string op, SqlFragment a, SqlFragment b)
    {
        var sql = $"({a.Sql}) {op} ({b.Sql})";
        var args = new List<object?>(a.Parameters.Count + b.Parameters.Count);
        args.AddRange(a.Parameters);
        args.AddRange(b.Parameters);
        return new SqlFragment(sql, args);
    }

    private static string QualifiedTable(Type modelType, VaultDocument doc)
    {
        var va = modelType.GetCustomAttribute<VaultAttribute>(inherit: true);
        var schema = string.IsNullOrWhiteSpace(va?.Keyspace) ? "public" : va!.Keyspace!.Trim();
        return $"{Q(schema)}.{Q(doc.Name)}";
    }

    private static string Col(VaultDocument doc, string logical)
        => doc.Columns.TryGetValue(logical, out var physical) ? physical : VaultDocument.ToCamelCase(logical);

    private static string Q(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
}

internal readonly record struct SqlFragment(string Sql, List<object?> Parameters)
{
    public SqlFragment(string sql) : this(sql, new List<object?>()) { }
}
