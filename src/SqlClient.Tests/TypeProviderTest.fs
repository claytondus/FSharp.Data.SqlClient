module FSharp.Data.TypeProviderTest

open System
open System.Data
open System.Data.SqlClient
open Xunit

[<Literal>]
let connection = ConnectionStrings.AdventureWorksNamed

type GetEvenNumbers = SqlCommandProvider<"select * from (values (2), (4), (8), (24)) as T(value)", connection>

[<Fact>]
let asyncSinlgeColumn() = 
    use cmd = new GetEvenNumbers()
    Assert.Equal<int[]>([| 2; 4; 8; 24 |], cmd.AsyncExecute() |> Async.RunSynchronously |> Seq.toArray)    

[<Fact>]
let ConnectionClose() = 
    use cmd = new GetEvenNumbers()
    let untypedCmd : ISqlCommand = upcast cmd
    let underlyingConnection = untypedCmd.Raw.Connection
    Assert.Equal(ConnectionState.Closed, underlyingConnection.State)
    Assert.Equal<int[]>([| 2; 4; 8;  24 |], cmd.Execute() |> Seq.toArray)    
    Assert.Equal(ConnectionState.Closed, underlyingConnection.State)

[<Fact>]
let ExternalInstanceConnection() = 
    use conn = new SqlConnection(ConnectionStrings.AdventureWorksLiteral)
    conn.Open()
    use cmd = new GetEvenNumbers(conn)
    let untypedCmd : ISqlCommand = upcast cmd
    let underlyingConnection = untypedCmd.Raw.Connection
    Assert.Equal(ConnectionState.Open, underlyingConnection.State)
    Assert.Equal<int[]>([| 2; 4; 8;  24 |], cmd.Execute() |> Seq.toArray)    
    Assert.Equal(ConnectionState.Open, underlyingConnection.State)


[<Fact>]
let TinyIntConversion() = 
    use cmd = new SqlCommandProvider<"SELECT CAST(10 AS TINYINT) AS Value", connection, SingleRow = true>()
    Assert.Equal(Some 10uy, cmd.Execute().Value)    

[<Fact>]
let ConditionalQuery() = 
    use cmd = new SqlCommandProvider<"IF @flag = 0 SELECT 1, 'monkey' ELSE SELECT 2, 'donkey'", connection, SingleRow=true, ResultType = ResultType.Tuples>()
    Assert.Equal(Some(1, "monkey"), cmd.Execute(flag = 0))    
    Assert.Equal(Some(2, "donkey"), cmd.Execute(flag = 1))    

[<Fact>]
let columnsShouldNotBeNull2() = 
    use cmd = new SqlCommandProvider<"
        SELECT COLUMN_NAME, IS_NULLABLE, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION
        FROM INFORMATION_SCHEMA.COLUMNS
        WHERE TABLE_NAME = 'DatabaseLog' and numeric_precision is null
        ORDER BY ORDINAL_POSITION
    ", connection, ResultType.Tuples, SingleRow = true>()

    let _,_,_,_,precision = cmd.Execute().Value
    Assert.Equal(None, precision)    

[<Literal>]
let bitCoinCode = "BTC"
[<Literal>]
let bitCoinName = "Bitcoin"

type DeleteBitCoin = SqlCommandProvider<"DELETE FROM Sales.Currency WHERE CurrencyCode = @Code", connection>
type InsertBitCoin = SqlCommandProvider<"INSERT INTO Sales.Currency VALUES(@Code, @Name, GETDATE())", connection>
type GetBitCoin = SqlCommandProvider<"SELECT CurrencyCode, Name FROM Sales.Currency WHERE CurrencyCode = @code", connection>

[<Fact>]
let asyncCustomRecord() =
    use cmd = new GetBitCoin()
    Assert.Equal(
        1,
        cmd.AsyncExecute("USD") |> Async.RunSynchronously |> Seq.length
    )

[<Fact>]
let singleRowOption() =
    use noneSingleton = new SqlCommandProvider<"select 1 where 1 = 0", connection, SingleRow = true>()
    Assert.IsNone <| noneSingleton.Execute()

    use someSingleton = new SqlCommandProvider<"select 1", connection, SingleRow = true>()
    Assert.Equal( Some 1, someSingleton.AsyncExecute() |> Async.RunSynchronously)

[<Fact>]
let ToTraceString() =
    let now = DateTime.Now
    let num = 42
    let expected = sprintf "exec sp_executesql N'SELECT CAST(@Date AS DATE), CAST(@Number AS INT)',N'@Date Date,@Number Int',@Date='%A',@Number='%d'" now num
    let cmd = new SqlCommandProvider<"SELECT CAST(@Date AS DATE), CAST(@Number AS INT)", connection, ResultType.Tuples>()
    Assert.Equal<string>(
        expected, 
        actual = cmd.ToTraceString( now, num)
    )

[<Fact>]
let ``ToTraceString for CRUD``() =    

    Assert.Equal<string>(
        expected = "exec sp_executesql N'SELECT CurrencyCode, Name FROM Sales.Currency WHERE CurrencyCode = @code',N'@code NChar(3)',@code='BTC'",
        actual = let cmd = new GetBitCoin() in cmd.ToTraceString( bitCoinCode)
    )
    
    Assert.Equal<string>(
        expected = "exec sp_executesql N'INSERT INTO Sales.Currency VALUES(@Code, @Name, GETDATE())',N'@Code NChar(3),@Name NVarChar(7)',@Code='BTC',@Name='Bitcoin'",
        actual = let cmd = new InsertBitCoin() in cmd.ToTraceString( bitCoinCode, bitCoinName)
    )

    Assert.Equal<string>(
        expected = "exec sp_executesql N'DELETE FROM Sales.Currency WHERE CurrencyCode = @Code',N'@Code NChar(3)',@Code='BTC'",
        actual = let cmd = new DeleteBitCoin() in cmd.ToTraceString( bitCoinCode)
    )
    
[<Fact>]
let ``ToTraceString double-quotes``() =    
    use cmd = new SqlCommandProvider<"SELECT OBJECT_ID('Sales.Currency')", connection>()
    let trace = cmd.ToTraceString()
    Assert.Equal<string>("exec sp_executesql N'SELECT OBJECT_ID(''Sales.Currency'')'", trace)

type LongRunning = SqlCommandProvider<"WAITFOR DELAY '00:00:35'; SELECT 42", connection, SingleRow = true>
[<Fact(
    Skip = "Don't execute for usual runs. Too slow."
    )>]
let CommandTimeout() =
    use cmd = new LongRunning(commandTimeout = 60)
    Assert.Equal(60, cmd.CommandTimeout)
    Assert.Equal(Some 42, cmd.Execute())     

type DynamicCommand = SqlCommandProvider<"
	    DECLARE @stmt AS NVARCHAR(MAX) = @tsql
	    DECLARE @params AS NVARCHAR(MAX) = N'@p1 nvarchar(100)'
	    DECLARE @p1 AS NVARCHAR(100) = @firstName
	    EXECUTE sp_executesql @stmt, @params, @p1
	    WITH RESULT SETS
	    (
		    (
			    Name NVARCHAR(100)
			    ,UUID UNIQUEIDENTIFIER 
		    )
	    )
    ", connection>

[<Fact>]
let DynamicSql() =    
    let cmd = new DynamicCommand()
    //provide dynamic sql query with param
    Assert.Equal(
        51,
        cmd.Execute("SELECT CONCAT(FirstName, LastName) AS Name, rowguid AS UUID FROM Person.Person WHERE FirstName = @p1", "Alex") |> Seq.toArray |> Array.length
    )
    //extend where condition by filetering out additional rows
    Assert. Equal(
        9,
        cmd.Execute("SELECT CONCAT(FirstName, LastName) AS Name, rowguid AS UUID FROM Person.Person WHERE FirstName = @p1 AND EmailPromotion = 2", "Alex") |> Seq.toArray |> Array.length
    )
    //accessing completely diff table
    Assert.Equal(
        1,
        cmd.Execute("SELECT Name, rowguid AS UUID FROM Production.Product WHERE Name = @p1", "Chainring Nut") |> Seq.toArray |> Array.length
    )

[<Fact>]
let DeleteStatement() =    
    use cmd = new SqlCommandProvider<"
        DECLARE @myTable TABLE( id INT)
        INSERT INTO @myTable VALUES (42)
        DELETE FROM @myTable
        ", connection>(ConnectionStrings.AdventureWorksLiteral)
    Assert.Equal(2, cmd.Execute())

[<Fact>]
let ``Setting the command timeout isn't overridden when giving connection context``() =
    let customTimeout = (Random()).Next(512, 1024)
    let getDate = new SqlCommandProvider<"select getdate()", ConnectionStrings.AdventureWorksLiteral>(commandTimeout = customTimeout)
    Assert.Equal(customTimeout, getDate.CommandTimeout)
    let sqlCommand = (getDate :> ISqlCommand).Raw
    Assert.Equal(customTimeout, sqlCommand.CommandTimeout)
