// WPF projects leave System.IO out of the implicit usings (Path clashes with System.Windows.Shapes.Path);
// files that use Shapes alias Path explicitly.
global using System.IO;
