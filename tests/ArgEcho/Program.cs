// Prints its arguments as one JSON line (non-ASCII escaped, so the console encoding does not matter).
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(args));
