// Prints the folder it runs in, then its arguments as one JSON line (non-ASCII escaped, so the console encoding
// does not matter).
Console.WriteLine("cwd " + System.Text.Json.JsonSerializer.Serialize(Environment.CurrentDirectory));
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(args));
