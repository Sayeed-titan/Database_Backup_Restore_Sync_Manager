using PgBackupManager.Core.Models;
using PgBackupManager.Core.Sql;

namespace PgBackupManager.Tests;

public class OracleStructuralTests
{
    private static string Ora(string sql) =>
        SqlCodeConverter.Convert(sql, DbEngine.Oracle, DbEngine.PostgreSql, new ConvertContext { SourceSchema = "HR", TargetSchema = "public" }).Sql;

    [Fact]
    public void OuterJoin_BecomesLeftJoin_WithConstantFilterInOn()
    {
        var s = Ora("SELECT e.name FROM emp e, dept d WHERE e.dept_id = d.id(+) AND d.active(+) = 1 AND e.sal > 0");
        Assert.Contains("FROM emp e\n  LEFT JOIN dept d ON e.dept_id = d.id AND d.active = 1", s.Replace("\r", ""));
        Assert.Contains("WHERE e.sal > 0", s);
        Assert.DoesNotContain("(+)", s);
    }

    [Fact]
    public void OuterJoin_BetweenAndIsNotSplit()
    {
        var s = Ora("SELECT 1 FROM a x, b y WHERE x.id = y.id(+) AND x.n BETWEEN 1 AND 5");
        Assert.Contains("WHERE x.n BETWEEN 1 AND 5", s);
    }

    [Fact]
    public void OuterJoin_TwoRequiredTables_UseCrossJoin()
    {
        var s = Ora("SELECT 1 FROM a x, c z, b y WHERE x.id = z.id AND x.id = y.id(+) AND z.k = y.k(+)");
        Assert.Contains("FROM a x\n  CROSS JOIN c z\n  LEFT JOIN b y ON x.id = y.id AND z.k = y.k", s.Replace("\r", ""));
        Assert.Contains("WHERE x.id = z.id", s);
    }

    [Fact]
    public void ConnectBy_BecomesRecursiveCte()
    {
        var s = Ora("SELECT LEVEL, name FROM emp START WITH mgr IS NULL CONNECT BY PRIOR id = mgr");
        Assert.Contains("WITH RECURSIVE pgbm_h AS (", s);
        Assert.Contains("SELECT emp.*, 1 AS level FROM emp emp WHERE mgr IS NULL", s);
        Assert.Contains("JOIN pgbm_h ON emp.mgr = pgbm_h.id", s);
        Assert.Contains("SELECT LEVEL, name FROM pgbm_h emp", s);
    }

    [Fact]
    public void ConnectBy_MultiTable_IsReportedNotMangled()
    {
        var r = SqlCodeConverter.Convert("SELECT 1 FROM emp e, dept d START WITH e.mgr IS NULL CONNECT BY PRIOR e.id = e.mgr", DbEngine.Oracle, DbEngine.PostgreSql, new ConvertContext());
        Assert.Contains(r.Warnings, w => w.Contains("CONNECT BY over several tables"));
        Assert.Contains("CONNECT BY", r.Sql);
    }

    [Fact]
    public void BulkCollect_Forall_Collections()
    {
        var s = Ora(@"CREATE OR REPLACE PROCEDURE hr.p IS
  TYPE t_ids IS TABLE OF NUMBER(10) INDEX BY PLS_INTEGER;
  v_ids t_ids;
BEGIN
  SELECT id BULK COLLECT INTO v_ids FROM emp WHERE sal > 0 ORDER BY id DESC;
  FORALL i IN INDICES OF v_ids
    DELETE FROM emp WHERE id = v_ids(i);
  v_ids.DELETE;
END;
/");
        Assert.Contains("v_ids bigint[];", s);
        Assert.Contains("SELECT array_agg(id ORDER BY id DESC)  INTO v_ids FROM emp WHERE sal > 0", s);
        Assert.Contains("FOR i IN 1 .. COALESCE(array_length(v_ids, 1), 0) LOOP", s);
        Assert.Contains("DELETE FROM emp WHERE id = v_ids[i];", s);
        Assert.Contains("v_ids := '{}';", s);
        Assert.DoesNotContain("BULK", s);
        Assert.DoesNotContain("FORALL", s);
    }

    [Fact]
    public void CollectionConstructor_BecomesArray()
    {
        var s = Ora(@"CREATE OR REPLACE PROCEDURE hr.p IS
  TYPE t_names IS VARRAY(10) OF VARCHAR2(20);
  v t_names := t_names('a', 'b');
BEGIN
  v := t_names();
END;
/");
        Assert.Contains("v varchar(20)[] := ARRAY['a', 'b'];", s);
        Assert.Contains("v := '{}';", s);
    }
}
