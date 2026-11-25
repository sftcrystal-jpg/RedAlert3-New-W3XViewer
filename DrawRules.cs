using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace W3xScene
{
    static class DrawRules
    {
        static XElement Schema(string name)
        {
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                return XElement.Load(stream);
        }
        static HashSet<string> Enum(XElement schema,string name)
        {
            return new HashSet<string>(schema.Elements().Single(e=>(string)e.Attribute("name")==name)
                .Descendants().Where(e=>e.Name.LocalName=="enumeration").Select(e=>(string)e.Attribute("value")),StringComparer.Ordinal);
        }
        static readonly XElement draw=Schema("ScriptedModelDraw.xsd"), basic=Schema("BaseModules.xsd");
        static readonly Dictionary<string,string> moduleTypes=Schema("GameObject.xsd").Elements().Single(e=>(string)e.Attribute("name")=="DrawModules")
            .Descendants().Where(e=>e.Name.LocalName=="element").ToDictionary(e=>(string)e.Attribute("name"),e=>(string)e.Attribute("type"));
        static readonly Dictionary<string,XElement> types=new[]{basic,draw}.Concat(
            Assembly.GetExecutingAssembly().GetManifestResourceNames().Where(n=>n.EndsWith("Draw.xsd") && n!="ScriptedModelDraw.xsd").Select(Schema))
            .SelectMany(s=>s.Elements()).Where(e=>e.Name.LocalName=="complexType").ToDictionary(e=>(string)e.Attribute("name"));
        public static bool IsModule(XElement e)
        {return (e.Name.Namespace==XNamespace.None || e.Name.NamespaceName=="uri:ea.com:eala:asset") && moduleTypes.ContainsKey(e.Name.LocalName);}
        public static readonly HashSet<string> Flags=Enum(Schema("ModelState.xsd"),"ModelConditionFlagType");
        public static readonly HashSet<string> Modes=Enum(Schema("AnimationMode.xsd"),"AnimationMode");
        static readonly HashSet<string> kinds=Enum(basic,"ParseCondStateType");
        static readonly HashSet<string> stateFlags=Enum(draw,"AnimationStateFlag");
        public static IEnumerable<XElement> Children(XElement e,string tag){return e.Elements(e.Name.Namespace+tag);}
        public static string A(XElement e,string key,string fallback="")
        {return e==null ? fallback : ((string)e.Attribute(key) ?? fallback);}
        public static string[] Words(string flags){return flags.Split((char[])null,StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();}
        public static string Label(XElement e)
        {
            string flags=A(e,"ConditionsYes");
            return (flags.Length>0 ? flags : A(e,"ParseCondStateType"))+"   "+A(e,"StateName",A(e,"Name"));
        }
        static IEnumerable<XElement> MatchingNormals(IEnumerable<XElement> states,HashSet<string> active)
        {
            return states.Where(s=>A(s,"ParseCondStateType")=="PARSE_NORMAL" && Words(A(s,"ConditionsYes")).All(active.Contains));
        }
        public static XElement FirstAnimationState(XElement module,HashSet<string> active)
        {
            var states=Children(module,"AnimationState");
            return MatchingNormals(states,active).FirstOrDefault() ?? states.FirstOrDefault(s=>A(s,"ParseCondStateType")=="PARSE_DEFAULT");
        }
        public static List<XElement> ModelMatches(XElement module,HashSet<string> active)
        {
            var all=Children(module,"ModelConditionState").ToArray();
            var groups=MatchingNormals(all,active).GroupBy(s=>Words(A(s,"ConditionsYes")).Length).OrderByDescending(g=>g.Key).FirstOrDefault();
            return groups==null ? all.Where(s=>A(s,"ParseCondStateType")=="PARSE_DEFAULT").ToList() : groups.ToList();
        }
        public static string Transition(XElement state,string previous)
        {
            if(previous.Length==0)return null;
            var script=Children(state,"Script").FirstOrDefault();
            if(script==null || !script.Value.Contains("CurDrawablePrevAnimationState()"))return null;
            foreach(Match m in Regex.Matches(script.Value,@"(?:if|elseif)\s+Prev\s*==\s*""([^""]+)""\s+then\s+CurDrawableSetTransitionAnimState\s*\(\s*""([^""]+)""\s*\)"))
                if(m.Groups[1].Value==previous) return m.Groups[2].Value;
            return null;
        }
        static IEnumerable<XElement> TypeHierarchy(string type)
        {
            XElement decl;
            while(type!=null && types.TryGetValue(type,out decl))
            {
                yield return decl;
                var parent=decl.Descendants().FirstOrDefault(e=>e.Name.LocalName=="extension");
                type=parent==null ? null : (string)parent.Attribute("base");
            }
        }
        public static bool SupportsAnimationStates(XElement module)
        {
            return IsModule(module) && TypeHierarchy(moduleTypes[module.Name.LocalName])
                .Any(t=>(string)t.Attribute("name")=="W3DScriptedModelDrawModuleData");
        }
        public static void InsertAnimationEntry(XElement parent,XElement entry)
        {
            // The SDK sequence puts states after model conditions and actions before scripts and events.
            bool state=entry.Name.LocalName=="AnimationState";
            var last=parent.Elements().TakeWhile(e=>e.Name==entry.Name || (state && e.Name==parent.Name.Namespace+"ModelConditionState")).LastOrDefault();
            var whitespace=(last==null ? parent.FirstNode : last.PreviousNode) as XText;
            string indent=whitespace!=null && string.IsNullOrWhiteSpace(whitespace.Value) ? whitespace.Value : null;
            if(last==null)parent.AddFirst(indent,entry);
            else last.AddAfterSelf(indent,entry);
        }
        static void CheckFields(XElement element,string type)
        {
            var attrs=new HashSet<string>();var children=new HashSet<string>();
            foreach(var decl in TypeHierarchy(type))
            {
                var fields=decl.Descendants();
                attrs.UnionWith(fields.Where(e=>e.Name.LocalName=="attribute").Select(e=>(string)e.Attribute("name")));
                children.UnionWith(fields.Where(e=>e.Name.LocalName=="element").Select(e=>(string)e.Attribute("name")));
            }
            foreach(var a in element.Attributes().Where(a=>!a.IsNamespaceDeclaration && a.Name.Namespace==XNamespace.None))
                if(!attrs.Contains(a.Name.LocalName)) throw new InvalidDataException(element.Name.LocalName+" 不支持属性 "+a.Name);
            foreach(var c in element.Elements())
                if(c.Name.Namespace!=element.Name.Namespace || !children.Contains(c.Name.LocalName)) throw new InvalidDataException(element.Name.LocalName+" 不支持子节点 "+c.Name);
        }
        public static void Validate(XElement module)
        {
            if(!IsModule(module))throw new InvalidDataException("请提供 RA3 Draws 下的一个绘制模块。");
            CheckFields(module,moduleTypes[module.Name.LocalName]);
            foreach(var s in module.Elements().Where(s=>s.Name.LocalName=="AnimationState" || s.Name.LocalName=="ModelConditionState"))
            {
                CheckFields(s,s.Name.LocalName);
                if(!kinds.Contains(A(s,"ParseCondStateType"))) throw new InvalidDataException("状态缺少有效的 ParseCondStateType。");
                foreach(var flag in Words(A(s,"ConditionsYes"))) if(!Flags.Contains(flag))throw new InvalidDataException("未知模型条件 "+flag);
                if(s.Name.LocalName=="AnimationState") foreach(var flag in Words(A(s,"Flags")))
                    if(!stateFlags.Contains(flag))throw new InvalidDataException("未知动画状态标志 "+flag);
                foreach(var a in Children(s,"Animation"))
                {
                    CheckFields(a,"Animation");
                    if(A(a,"AnimationName")=="" || !Modes.Contains(A(a,"AnimationMode","LOOP")))
                        throw new InvalidDataException("请检查 AnimationName 和 AnimationMode。");
                }
            }
        }
    }
}
