IF OBJECT_ID('dbo.CollectionsTowers') IS NULL
CREATE TABLE dbo.CollectionsTowers (TowerId int IDENTITY(1,1) PRIMARY KEY, TowerNumber int NOT NULL, TowerName nvarchar(200) NOT NULL, CompanyId int NOT NULL, IsActive bit NOT NULL DEFAULT 1);
IF NOT EXISTS (SELECT 1 FROM dbo.CollectionsTowers)
BEGIN
  INSERT dbo.CollectionsTowers (TowerNumber, TowerName, CompanyId)
  SELECT n, 'Tower ' + CAST(n AS varchar(10)), 4 FROM (SELECT 100 + ROW_NUMBER() OVER (ORDER BY (SELECT 1)) n FROM sys.all_objects) x WHERE n BETWEEN 101 AND 130 AND n <> 119;
  INSERT dbo.CollectionsTowers (TowerNumber, TowerName, CompanyId) VALUES (127, 'Faradis', 32), (140, 'Al Ghaf', 32);
END
