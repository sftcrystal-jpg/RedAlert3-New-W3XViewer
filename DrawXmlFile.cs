using System;
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

        byte[] ReplaceModule(int[] elementPath,XElement module)
        {
            string text=Decode(saved);
            var target=ElementAtPath(text,elementPath,LoadOptions.PreserveWhitespace|LoadOptions.SetLineInfo);
            int start=LineOffset(text,target)-1;
            if(!DrawRules.IsModule(target) || start<0 || text[start]!='<')throw new InvalidDataException("无法定位原文件中的 Draw。");
            int last=start;
            if(!target.IsEmpty)
            {
                using(var reader=target.CreateReader())
                {
                    while(reader.Read() && !(reader.NodeType==XmlNodeType.EndElement && reader.Depth==0)){}
                    last=LineOffset(text,(IXmlLineInfo)reader);
                }
            }
            int end=TagEnd(text,last);
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
                output.Write(saved,0,prefix);
                fragment.Position=encoding.GetPreamble().Length;
                fragment.CopyTo(output);
                output.Write(saved,suffix,saved.Length-suffix);
                return output.ToArray();
            }
        }

        public void Save(int[] elementPath,XElement module)
        {
            var updated=ReplaceModule(elementPath,module);
            var result=ElementAtPath(Decode(updated),elementPath,LoadOptions.None);
            if(!DrawRules.IsModule(result) || result.Name.LocalName!=module.Name.LocalName || DrawRules.A(result,"id")!=DrawRules.A(module,"id"))
                throw new InvalidDataException("写回结果与所选 Draw 不一致。");
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
                }
            }
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
    }
}
