# Protocol serialization checks

```powershell
dotnet run --project tests/ProtocolSerialization -c Release
```

The 12 checks exercise production QNetZ serialization directly with fixed bytes and round trips. Coverage includes unsigned and signed 64-bit boundaries, packed date and time fields, leap days, years through 9999, and the existing fallback for invalid wire dates. A historical date vector checks that `14 72 DD 6E 1F 00 00 00` decodes as **2011-11-14 23:08:20**.
