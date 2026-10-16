using System.Text;

namespace PrintMaster.Services;

/// <summary>
/// Adapts standard environment notation to the matrix/cases atoms already implemented by
/// XamlMath 2.1.0. This only constructs parser input; the editable Markdown source is retained.
/// A lexical stack is necessary because an environment can contain groups, text, escapes and
/// other environments, including several nested environments with the same name.
/// </summary>
internal static class MarkdownFormulaEnvironments
{
    private readonly record struct EnvironmentFrame(string Name,int GroupDepth);

    internal static string Normalize(string source)
    {
        var output=new StringBuilder(source.Length);var environments=new Stack<EnvironmentFrame>();var groupDepth=0;
        for(var position=0;position<source.Length;)
        {
            var character=source[position];
            if(character!='\\')
            {
                if(character=='{')groupDepth++;
                else if(character=='}' && --groupDepth<0)throw new FormatException("Закрывающая фигурная скобка без открывающей.");
                output.Append(character);position++;continue;
            }
            var start=position++;
            if(position==source.Length){output.Append('\\');continue;}
            if(!char.IsLetter(source[position]))
            {
                // Includes \\, \{, \}, \&: escaped characters never introduce syntax here.
                output.Append(source.AsSpan(start,2));position++;continue;
            }
            while(position<source.Length && char.IsLetter(source[position]))position++;
            var command=source.AsSpan(start+1,position-start-1);
            if(command.SequenceEqual("text"))
            {
                var textStart=position;SkipWhitespace(source,ref textStart);
                if(textStart<source.Length && source[textStart]=='{')position=GroupEnd(source,textStart);
                output.Append(source.AsSpan(start,position-start));continue;
            }
            if(!command.SequenceEqual("begin") && !command.SequenceEqual("end"))
            {
                output.Append(source.AsSpan(start,position-start));continue;
            }
            var nameStart=position;SkipWhitespace(source,ref nameStart);
            if(nameStart==source.Length || source[nameStart]!='{')
                throw new FormatException("После \\"+command.ToString()+" требуется имя окружения в фигурных скобках.");
            position=GroupEnd(source,nameStart);
            var name=source[(nameStart+1)..(position-1)];
            if(name.Length==0 || name.Any(c=>!char.IsLetter(c) && c!='*'))
                throw new FormatException("Некорректное имя математического окружения: «"+name+"».");
            if(command.SequenceEqual("begin"))
            {
                if(environments.Count>=64)throw new FormatException("Слишком много вложенных математических окружений (не более 64).");
                environments.Push(new(name,groupDepth));
                output.Append(name switch
                {
                    "matrix"=>@"\matrix{",
                    // XamlMath 2.1.0's native pmatrix uses lbrack/rbrack. Explicit fences retain
                    // the standard LaTeX distinction between round, square and brace matrices.
                    "pmatrix"=>@"\left(\matrix{",
                    "bmatrix"=>@"\left[\matrix{",
                    "Bmatrix"=>@"\left\{\matrix{",
                    "vmatrix"=>@"\left|\matrix{",
                    "Vmatrix"=>@"\left\Vert\matrix{",
                    "cases"=>@"\cases{",
                    _=>source[start..position]
                });
            }
            else
            {
                if(!environments.TryPop(out var opened))
                    throw new FormatException("Окружение \\end{"+name+"} не было открыто.");
                if(opened.Name!=name)
                    throw new FormatException("Ожидалось \\end{"+opened.Name+"}, получено \\end{"+name+"}.");
                if(groupDepth!=opened.GroupDepth)
                    throw new FormatException("Внутри окружения «"+name+"» не закрыта фигурная скобка.");
                output.Append(name switch
                {
                    "matrix"=>"}",
                    "pmatrix"=>@"}\right)",
                    "bmatrix"=>@"}\right]",
                    "Bmatrix"=>@"}\right\}",
                    "vmatrix"=>@"}\right|",
                    "Vmatrix"=>@"}\right\Vert",
                    "cases"=>"}",
                    _=>source[start..position]
                });
            }
        }
        if(environments.TryPeek(out var unclosed))throw new FormatException("Не найдено \\end{"+unclosed.Name+"}.");
        if(groupDepth!=0)throw new FormatException("Не закрыта фигурная скобка в формуле.");
        return output.ToString();
    }

    private static void SkipWhitespace(string source,ref int position)
    {
        while(position<source.Length && char.IsWhiteSpace(source[position]))position++;
    }

    private static int GroupEnd(string source,int start)
    {
        var depth=1;
        for(var position=start+1;position<source.Length;position++)
        {
            if(source[position]=='\\' && position+1<source.Length){position++;continue;}
            if(source[position]=='{')depth++;
            else if(source[position]=='}' && --depth==0)return position+1;
        }
        throw new FormatException("Не закрыта фигурная скобка в формуле.");
    }
}
