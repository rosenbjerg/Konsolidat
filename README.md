# Konsolidat
Simple CLI tool for consolidating nuget package version conflicts in csproj files



## Usage
Requires 3 input parameters:
```
-s, --sln           Required. Path to the solution file
-r, --rid           Required. dotnet runtime identifier
-n, --namespaces    Required. Project namespace regex
```

### Example arguments
`-s /path/to/MySolution.sln -r linux-x64 -n ^MyNamespace`

