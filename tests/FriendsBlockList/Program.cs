using QNetZ;

QConfiguration.Instance = QConfiguration.MakeDevelopmentConfiguration("127.0.0.1");
QLog.LogFunction = null;
return FriendsBlockListChecks.Run();
