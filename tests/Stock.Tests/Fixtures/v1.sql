CREATE TABLE Product(Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Spec TEXT NOT NULL, Unit TEXT NOT NULL,
                NameKey TEXT NOT NULL, SpecKey TEXT NOT NULL, Active INTEGER NOT NULL CHECK(Active IN(0,1)), UNIQUE(NameKey,SpecKey));
            CREATE TABLE StockBalance(ProductId INTEGER PRIMARY KEY REFERENCES Product(Id), Warehouse INTEGER NOT NULL, Store INTEGER NOT NULL,
                CHECK(Warehouse>=0 AND Store>=0 AND Warehouse+Store<=2147483647));
            CREATE TABLE Document(Id TEXT PRIMARY KEY, Number TEXT NOT NULL UNIQUE, Kind TEXT NOT NULL, Channel TEXT NOT NULL, Status TEXT NOT NULL,
                BusinessDate TEXT NOT NULL, OccurredAt TEXT NOT NULL, SubmissionKey TEXT NOT NULL UNIQUE, OriginalId TEXT, VoidId TEXT, VoidAt TEXT, Reason TEXT NOT NULL DEFAULT '');
            CREATE TABLE DocumentLine(Id INTEGER PRIMARY KEY, DocumentId TEXT NOT NULL REFERENCES Document(Id) ON DELETE CASCADE,
                ProductId INTEGER NOT NULL REFERENCES Product(Id), Name TEXT NOT NULL, Spec TEXT NOT NULL, Unit TEXT NOT NULL, Quantity INTEGER NOT NULL,
                WarehouseDelta INTEGER NOT NULL, StoreDelta INTEGER NOT NULL);
            CREATE TABLE Attachment(Id INTEGER PRIMARY KEY, DocumentId TEXT NOT NULL REFERENCES Document(Id) ON DELETE CASCADE, Path TEXT NOT NULL UNIQUE);
            CREATE TABLE CarryForward(ProductId INTEGER PRIMARY KEY REFERENCES Product(Id), Warehouse INTEGER NOT NULL, Store INTEGER NOT NULL);
            CREATE TABLE Metadata(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            CREATE INDEX IX_Document_Date ON Document(BusinessDate,OccurredAt);
            CREATE INDEX IX_Line_Product ON DocumentLine(ProductId,DocumentId);
            INSERT INTO Metadata VALUES('Cutoff','0001-01-01');
            PRAGMA user_version=1;