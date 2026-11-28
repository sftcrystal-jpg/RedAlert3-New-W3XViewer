using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace W3xScene
{
    // All Draws imported from a file share the last successfully saved byte snapshot.
    sealed class DrawXmlFile
    {
        public readonly string FilePath;
        public string BackupPath {get{return FilePath+".w3xviewer.bak";}}
        public readonly XElement[] Modules;
        readonly Encoding encoding;
        readonly int preamble;
        byte[] saved;

        public DrawXmlFile(string path)
        {
            FilePath=Path.GetFullPath(path);
            saved=File.ReadAllBytes(FilePath);
            int codePage;
            using(var reader=new XmlTextReader(new MemoryStream(saved)){XmlResolver=null})
            {
                if(!reader.Read())throw new InvalidDataException("XML 文件为空。");
                codePage=reader.Encoding.CodePage;
            }
            // Some SDK files declare ASCII while storing Chinese comments in UTF-8 or GBK.
            if(codePage==20127 && saved.Any(b=>b>=128))
            {
                try{new UTF8Encoding(false,true).GetCharCount(saved);codePage=65001;}
                catch(DecoderFallbackException){codePage=936;}
            }
            encoding=Encoding.GetEncoding(codePage,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
            var bom=encoding.GetPreamble();
            preamble=saved.Take(bom.Length).SequenceEqual(bom) ? bom.Length : 0;
            Modules=XElement.Parse(Decode(saved),LoadOptions.PreserveWhitespace).DescendantsAndSelf().Where(DrawRules.IsModule).ToArray();
        }

        string Decode(byte[] bytes){return encoding.GetString(bytes,preamble,bytes.Length-preamble);}
        public static int[] ElementPath(XElement e)
        {return e.AncestorsAndSelf().Where(n=>n.Parent!=null).Reverse().Select(n=>n.ElementsBeforeSelf().Count()).ToArray();}

        static XElement ElementAtPath(string text,int[] path,LoadOptions options)
        {
            var element=XElement.Parse(text,options);
            foreach(int index in path)element=element.Elements().ElementAt(index);
            return element;
        }

        // XmlReader positions count characters from the first column of each physical line.
        static int LineOffset(string text,IXmlLineInfo info)
        {
            int offset=0;
            for(int line=1;line<info.LineNumber;line++)
            {
                while(text[offset]!='\r' && text[offset]!='\n')offset++;
                if(text[offset++]=='\r' && offset<text.Length && text[offset]=='\n')offset++;
            }
            return offset+info.LinePosition-1;
        }

        static int TagEnd(string text,int offset)
        {
            char quote='\0';
            for(int i=offset;i<text.Length;i++)
            {
                char c=text[i];
                if(quote!='\0'){if(c==quote)quote='\0';}
                else if(c=='"' || c=='\'')quote=c;
                else if(c=='>')return i+1;
            }
            throw new InvalidDataException("无法定位原文件中的 Draw 结束标签。");
        }

        static int ElementEnd(string text,XElement target,int start)
        {
            int last=start;
            if(!target.IsEmpty)
            {
                using(var reader=target.CreateReader())
                {
                    while(reader.Read() && !(reader.NodeType==XmlNodeType.EndElement && reader.Depth==0)){}
                    last=LineOffset(text,(IXmlLineInfo)reader);
                }
            }
            return TagEnd(text,last);
        }
        byte[] WriteModule(byte[] bytes,int[] path,XElement module,bool append,out int[] resultingPath)
        {
            string text=Decode(bytes);
            var target=ElementAtPath(text,path,LoadOptions.PreserveWhitespace|LoadOptions.SetLineInfo);
            resultingPath=path;
            if(append)
            {
                if(target.Name.LocalName!="Draws")throw new InvalidDataException("新增 Draw 必须写入单位的 Draws 节点。");
                if(target.Elements().Any(e=>Import.Names.Equals(DrawRules.A(e,"id"),DrawRules.A(module,"id"))))
                    throw new InvalidDataException("同组已有同名 Draw："+DrawRules.A(module,"id"));
                resultingPath=path.Concat(new[]{target.Elements().Count()}).ToArray();
                target=target.Elements().Last();
            }
            else if(!DrawRules.IsModule(target))throw new InvalidDataException("无法定位原文件中的 Draw。");
            int start=LineOffset(text,target)-1;
            if(start<0 || text[start]!='<')throw new InvalidDataException("无法定位原文件中的 XML 节点。");
            int end=ElementEnd(text,target,start);
            int firstBreak=text.IndexOfAny(new[]{'\r','\n'});
            string newline=firstBreak<0 ? Environment.NewLine : text[firstBreak]=='\n' ? "\n" :
                firstBreak+1<text.Length && text[firstBreak+1]=='\n' ? "\r\n" : "\r";
            using(var output=new MemoryStream())
            using(var fragment=new MemoryStream())
            {
                var settings=new XmlWriterSettings{Encoding=encoding,NewLineChars=newline,Indent=true,OmitXmlDeclaration=true};
                using(var writer=XmlWriter.Create(fragment,settings))module.WriteTo(writer);
                // Keep the exact bytes outside the Draw, including the original file's BOM and declaration.
                int prefix=preamble+encoding.GetByteCount(text.Substring(0,start));
                int suffix=prefix+encoding.GetByteCount(text.Substring(start,end-start));
                output.Write(bytes,0,append ? suffix : prefix);
                if(append)
                {
                    int lineStart=text.LastIndexOfAny(new[]{'\r','\n'},start)+1;
                    string indent=text.Substring(lineStart,start-lineStart);
                    byte[] spacing=encoding.GetBytes(newline+(string.IsNullOrWhiteSpace(indent)?indent:""));
                    output.Write(spacing,0,spacing.Length);
                }
                fragment.Position=encoding.GetPreamble().Length;
                fragment.CopyTo(output);
                output.Write(bytes,suffix,bytes.Length-suffix);
                return output.ToArray();
            }
        }

        public void Save(DrawPreview[] changes)
        {
            if(changes.Length==0)return;
            var updated=saved;
            var paths=new Dictionary<DrawPreview,int[]>();
            foreach(var draw in changes)
            {
                if(draw.SourceFile!=this || draw.Draft!=draw.Applied)throw new InvalidDataException("请先应用同组待写回 Draw 的 XML 草稿。");
                DrawRules.Validate(draw.Module);
                int[] path;
                bool append=draw.SourceElementPath==null;
                updated=WriteModule(updated,append?ElementPath(draw.Group):draw.SourceElementPath,draw.Module,append,out path);
                var result=ElementAtPath(Decode(updated),path,LoadOptions.None);
                if(!DrawRules.IsModule(result) || result.Name.LocalName!=draw.Module.Name.LocalName || DrawRules.A(result,"id")!=DrawRules.A(draw.Module,"id"))
                    throw new InvalidDataException("写回结果与所选 Draw 不一致。");
                paths.Add(draw,path);
            }
            string temp=Path.Combine(Path.GetDirectoryName(FilePath),Guid.NewGuid().ToString("N")+".tmp");
            try
            {
                File.WriteAllBytes(temp,updated);
                // Serialize competing viewer saves and refuse stale drafts before replacing the file.
                using(new FileStream(FilePath+".w3xviewer.lock",FileMode.CreateNew,FileAccess.Write,FileShare.None,1,FileOptions.DeleteOnClose))
                using(var original=new FileStream(FilePath,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
                using(var snapshot=new MemoryStream())
                {
                    original.CopyTo(snapshot);
                    if(!saved.SequenceEqual(snapshot.ToArray()))
                        throw new IOException("原文件已被其他程序修改。请先复制当前修改，重新打开原文件后再应用。");
                    File.Replace(temp,FilePath,BackupPath);
                    saved=updated;
                    foreach(var entry in paths){entry.Key.SourceElementPath=entry.Value;entry.Key.Saved=entry.Key.Applied;}
                }
            }
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
    }
}
