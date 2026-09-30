using PgBackupManager.Core.Models;
using PgBackupManager.Core.Sql;
using Xunit.Abstractions;

namespace PgBackupManager.Tests;

public class ConverterTests
{
    private readonly ITestOutputHelper _out;
    public ConverterTests(ITestOutputHelper output) => _out = output;

    private string Ora(string src, string? target = "app")
    {
        var r = SqlCodeConverter.Convert(src, DbEngine.Oracle, DbEngine.PostgreSql, new ConvertContext { SourceSchema = "HR", TargetSchema = target });
        _out.WriteLine(r.Sql);
        return r.Sql;
    }

    [Fact]
    public void Oracle_Function_Builtins_And_Structure()
    {
        var sql = Ora(@"CREATE OR REPLACE FUNCTION ""HR"".""GET_NAME"" (p_id IN NUMBER, p_def VARCHAR2 DEFAULT 'x') RETURN VARCHAR2 IS
  v_name VARCHAR2(100 CHAR);
  v_cnt PLS_INTEGER := 0;
  CURSOR c_emp IS SELECT ename FROM emp WHERE deptno = p_id;
BEGIN
  SELECT NVL(ename, p_def), DECODE(status, 'A', 'Active', NULL, 'Unknown', 'Other') INTO v_name, v_name FROM emp WHERE id = p_id AND hired < SYSDATE;
  FOR r IN (SELECT id FROM emp) LOOP
    v_cnt := v_cnt + 1;
  END LOOP;
  log_it(v_name);
  RETURN v_name;
EXCEPTION
  WHEN NO_DATA_FOUND THEN
    RAISE_APPLICATION_ERROR(-20001, 'not found: ' || p_id);
END GET_NAME;
/");
        Assert.Contains("CREATE OR REPLACE FUNCTION app.get_name(p_id numeric, p_def varchar DEFAULT 'x')", sql);
        Assert.Contains("RETURNS varchar", sql);
        Assert.Contains("LANGUAGE plpgsql", sql);
        Assert.Contains("COALESCE(ename, p_def)", sql);
        Assert.Contains("CASE WHEN status = 'A' THEN 'Active' WHEN status IS NULL THEN 'Unknown' ELSE 'Other' END", sql);
        Assert.Contains("LOCALTIMESTAMP(0)", sql);
        Assert.Contains("v_cnt integer := 0", sql);
        Assert.Contains("c_emp CURSOR  FOR SELECT".Replace("  ", " "), sql);
        Assert.Contains("FOR r IN SELECT id FROM emp LOOP", sql);
        Assert.Contains("r RECORD;", sql);
        Assert.Contains("PERFORM log_it(v_name);", sql);
        Assert.Contains("RAISE EXCEPTION USING MESSAGE = 'not found: ' || p_id, ERRCODE = 'P0001', HINT = 'ORA-20001'", sql);
        Assert.Contains("END;\n$body$;", sql.Replace("\r", ""));
        Assert.DoesNotContain("END GET_NAME", sql);
    }

    [Fact]
    public void Oracle_Package_BecomesSchema()
    {
        var sql = Ora(@"CREATE OR REPLACE PACKAGE BODY hr.pkg_pay AS
  g_rate NUMBER := 1.5;
  PROCEDURE recalc(p_id NUMBER) IS
  BEGIN
    UPDATE pay SET amount = amount * 2 WHERE id = p_id;
    COMMIT;
  END recalc;
  FUNCTION total RETURN NUMBER IS
    v NUMBER;
  BEGIN
    recalc(1);
    SELECT SUM(amount) INTO v FROM pay;
    RETURN v;
  END;
END pkg_pay;
/");
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS pkg_pay;", sql);
        Assert.Contains("CREATE OR REPLACE PROCEDURE pkg_pay.recalc(p_id numeric)", sql);
        Assert.Contains("CREATE OR REPLACE FUNCTION pkg_pay.total()", sql);
        Assert.Contains("CALL recalc(1);", sql);
        Assert.Contains("package-level", sql);
    }

    [Fact]
    public void Oracle_Trigger_SplitsIntoFunction()
    {
        var sql = Ora(@"CREATE OR REPLACE TRIGGER hr.trg_emp_bi BEFORE INSERT OR UPDATE ON hr.emp FOR EACH ROW
BEGIN
  IF INSERTING THEN
    :NEW.id := emp_seq.NEXTVAL;
  END IF;
  :NEW.updated := SYSDATE;
END;
/");
        Assert.Contains("RETURNS trigger", sql);
        Assert.Contains("NEW.id := nextval('emp_seq');", sql);
        Assert.Contains("(TG_OP = 'INSERT')", sql);
        Assert.Contains("RETURN NEW;", sql);
        Assert.Contains("CREATE TRIGGER trg_emp_bi BEFORE INSERT OR UPDATE ON app.emp FOR EACH ROW EXECUTE FUNCTION app.trg_emp_bi_fn();", sql);
    }

    [Fact]
    public void Oracle_Sequence_View_Table()
    {
        var sql = Ora(@"CREATE SEQUENCE ""HR"".""EMP_SEQ"" MINVALUE 1 MAXVALUE 9999999999999999999999999999 INCREMENT BY 1 START WITH 41 CACHE 20 NOORDER NOCYCLE NOKEEP NOSCALE GLOBAL;
CREATE OR REPLACE FORCE EDITIONABLE VIEW ""HR"".""V_EMP"" (""ID"", ""NAME"") AS SELECT id, name FROM emp WHERE ROWNUM < 10 AND x = TO_DATE('2020-01-01', 'YYYY-MM-DD') FROM DUAL WITH READ ONLY;
CREATE TABLE ""HR"".""EMP"" (""ID"" NUMBER(10,0) NOT NULL ENABLE, ""NAME"" VARCHAR2(50 BYTE), ""HIRED"" DATE, ""PIC"" BLOB, CONSTRAINT ""EMP_PK"" PRIMARY KEY (""ID"") USING INDEX PCTFREE 10 TABLESPACE ""USERS"" ENABLE) SEGMENT CREATION IMMEDIATE TABLESPACE ""USERS"";");
        Assert.Contains("CREATE SEQUENCE IF NOT EXISTS app.emp_seq MINVALUE 1 INCREMENT BY 1 START WITH 41 CACHE 20 NO CYCLE;", sql);
        Assert.Contains("CREATE OR REPLACE VIEW app.v_emp (id, name) AS", sql);
        Assert.Contains("id bigint NOT NULL", sql);
        Assert.Contains("name varchar(50)", sql);
        Assert.Contains("hired timestamp(0)", sql);
        Assert.Contains("pic bytea", sql);
        Assert.DoesNotContain("TABLESPACE", sql);
        Assert.DoesNotContain("PCTFREE", sql);
    }

    [Fact]
    public void TSql_Procedure_ControlFlow()
    {
        var r = SqlCodeConverter.Convert(@"CREATE PROCEDURE dbo.usp_Bonus @EmpId INT, @Pct DECIMAL(5,2) = 10, @Result NVARCHAR(100) OUTPUT
AS
BEGIN
    SET NOCOUNT ON
    DECLARE @Salary MONEY, @Name NVARCHAR(50) = N'x'
    SELECT @Salary = Salary, @Name = Name FROM dbo.Employees WITH (NOLOCK) WHERE Id = @EmpId
    IF @Salary IS NULL
    BEGIN
        RAISERROR('Employee not found', 16, 1)
        RETURN
    END
    ELSE
        UPDATE dbo.Employees SET Salary = Salary * (1 + @Pct / 100), Updated = GETDATE() WHERE Id = @EmpId
    WHILE @Pct > 0
    BEGIN
        SET @Pct = @Pct - 1
    END
    BEGIN TRY
        EXEC dbo.usp_Log @EmpId, @Msg = 'done'
    END TRY
    BEGIN CATCH
        PRINT ERROR_MESSAGE()
    END CATCH
    SET @Result = ISNULL(@Name, '') + ' updated'
END", DbEngine.SqlServer, DbEngine.PostgreSql, new ConvertContext { SourceSchema = "dbo", TargetSchema = "app" });
        _out.WriteLine(r.Sql);
        var sql = r.Sql;
        Assert.Contains("CREATE OR REPLACE PROCEDURE app.usp_bonus(p_empid INT, p_pct DECIMAL(5, 2) DEFAULT 10, INOUT p_result varchar(100))", sql);
        Assert.Contains("v_salary numeric(19,4);", sql);
        Assert.Contains("v_name := 'x';", sql);
        Assert.Contains("SELECT salary, name INTO v_salary, v_name FROM app.employees", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NOLOCK", sql);
        Assert.Contains("IF v_salary IS NULL THEN", sql);
        Assert.Contains("RAISE EXCEPTION '%', 'Employee not found';", sql);
        Assert.Contains("ELSE", sql);
        Assert.Contains("END IF;", sql);
        Assert.Contains("WHILE p_pct > 0 LOOP", sql);
        Assert.Contains("p_pct := p_pct - 1;", sql);
        Assert.Contains("EXCEPTION WHEN OTHERS THEN", sql);
        Assert.Contains("CALL app.usp_log(p_empid, p_msg => 'done');", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LOCALTIMESTAMP", sql);
        Assert.Contains("p_result := COALESCE(v_name, '') || ' updated';", sql);
    }

    [Fact]
    public void TSql_View_TopToLimit()
    {
        var r = SqlCodeConverter.Convert("CREATE VIEW [dbo].[vTop] AS SELECT TOP (5) [Id], LEN([Name]) AS L FROM dbo.T ORDER BY Id", DbEngine.SqlServer, DbEngine.PostgreSql,
            new ConvertContext { SourceSchema = "dbo", TargetSchema = "app" });
        _out.WriteLine(r.Sql);
        Assert.Contains("CREATE OR REPLACE VIEW app.vtop AS", r.Sql);
        Assert.Contains("length(name)", r.Sql);
        Assert.Contains("LIMIT 5;", r.Sql);
    }

    [Fact]
    public void MySql_Table_And_Limit()
    {
        var r = SqlCodeConverter.Convert("CREATE TABLE `users` (`id` int unsigned NOT NULL AUTO_INCREMENT, `ok` tinyint(1) DEFAULT 0, `name` varchar(20) CHARACTER SET utf8mb4 COMMENT 'n', PRIMARY KEY (`id`), KEY `ix` (`name`)) ENGINE=InnoDB;\nSELECT IFNULL(a, 1) FROM t LIMIT 10, 5;",
            DbEngine.MySql, DbEngine.PostgreSql, new ConvertContext());
        _out.WriteLine(r.Sql);
        Assert.Contains("id integer NOT NULL GENERATED BY DEFAULT AS IDENTITY", r.Sql);
        Assert.Contains("ok boolean DEFAULT 0", r.Sql);
        Assert.Contains("LIMIT 5 OFFSET 10", r.Sql);
        Assert.Contains("COALESCE(a, 1)", r.Sql);
    }

    [Fact]
    public void Postgres_To_SqlServer_View()
    {
        var r = SqlCodeConverter.Convert("CREATE VIEW v AS SELECT id::text, now(), name || 'x' FROM t WHERE active = true LIMIT 10;", DbEngine.PostgreSql, DbEngine.SqlServer, new ConvertContext());
        _out.WriteLine(r.Sql);
        Assert.Contains("CAST(id AS nvarchar(max))", r.Sql);
        Assert.Contains("SYSDATETIME()", r.Sql);
        Assert.Contains("name + 'x'", r.Sql);
        Assert.Contains("active = 1", r.Sql);
    }

    [Fact]
    public void SameEngine_RenamesSchemaOnly()
    {
        var r = SqlCodeConverter.Convert("CREATE VIEW old_s.v AS SELECT * FROM old_s.t JOIN other.u ON true;", DbEngine.PostgreSql, DbEngine.PostgreSql, new ConvertContext { SourceSchema = "old_s", TargetSchema = "new_s" });
        Assert.Equal("CREATE VIEW new_s.v AS SELECT * FROM new_s.t JOIN other.u ON true;", r.Sql);
    }
}
