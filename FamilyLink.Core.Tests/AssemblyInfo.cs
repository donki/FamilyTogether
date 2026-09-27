// SQLiteAsyncConnection.ResetPool() es global: las pruebas que abren SQLite no pueden ir en paralelo.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
