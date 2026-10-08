/*
  V003 - Tower number from a unit code.
    TP124-1001 -> 124      124-1001 -> 124      TP136-C-402 -> 136      136-C-402 -> 136
  Rule: trim, remove ONE optional leading "TP" (case-insensitive), take the text before the first hyphen, trim.
  Returns NULL for blank input or an empty component. Mirrored by TigerCS.Domain CollectionsTowerNumber.Parse (unit-tested).
*/
CREATE OR ALTER FUNCTION dbo.fn_CollectionsTowerNumber (@UnitCode nvarchar(200))
RETURNS nvarchar(20)
AS
BEGIN
    DECLARE @s nvarchar(200) = LTRIM(RTRIM(@UnitCode));
    IF @s IS NULL OR @s = N'' RETURN NULL;
    IF UPPER(LEFT(@s, 2)) = N'TP' SET @s = LTRIM(SUBSTRING(@s, 3, 200));
    DECLARE @p int = CHARINDEX(N'-', @s);
    IF @p > 0 SET @s = LEFT(@s, @p - 1);
    SET @s = LTRIM(RTRIM(@s));
    IF @s = N'' RETURN NULL;
    RETURN LEFT(@s, 20);
END;
