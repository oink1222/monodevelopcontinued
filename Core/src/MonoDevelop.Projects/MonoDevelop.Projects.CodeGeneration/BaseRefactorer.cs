//
// BaseRefactorer.cs
//
// Author:
//   Lluis Sanchez Gual
//
// Copyright (C) 2005 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.IO;
using System.CodeDom;
using System.CodeDom.Compiler;
using Microsoft.CSharp;

using MonoDevelop.Projects.Parser;
using MonoDevelop.Projects.Text;
using MonoDevelop.Projects.CodeGeneration;

namespace MonoDevelop.Projects.CodeGeneration
{
	public abstract class BaseRefactorer: IRefactorer
	{
		public virtual RefactorOperations SupportedOperations {
			get { return RefactorOperations.All; }
		}
		
		protected abstract ICodeGenerator GetGenerator ();
	
		public IClass CreateClass (RefactorerContext ctx, string directory, string namspace, CodeTypeDeclaration type)
		{
			CodeCompileUnit unit = new CodeCompileUnit ();
			CodeNamespace ns = new CodeNamespace (namspace);
			ns.Types.Add (type);
			unit.Namespaces.Add (ns);
			
			string file = Path.Combine (directory, type.Name + ".cs");
			StreamWriter sw = new StreamWriter (file);
			
			ICodeGenerator gen = GetGenerator ();
			gen.GenerateCodeFromCompileUnit (unit, sw, GetOptions (false));
			
			sw.Close ();
			
			IParseInformation pi = ctx.ParserContext.ParseFile (file);
			ClassCollection clss = ((ICompilationUnit)pi.BestCompilationUnit).Classes;
			if (clss.Count > 0)
				return clss [0];
			else
				throw new Exception ("Class creation failed. The parser did not find the created class.");
		}
		
		public virtual IClass RenameClass (RefactorerContext ctx, IClass cls, string newName)
		{
			return null;
		}
		
		public virtual MemberReferenceCollection FindClassReferences (RefactorerContext ctx, string file, IClass cls)
		{
			return null;
		}
		
		public virtual IMember AddMember (RefactorerContext ctx, IClass cls, CodeTypeMember member)
		{
			IEditableTextFile buffer = ctx.GetFile (cls.Region.FileName);
			
			int pos = GetNewMemberPosition (buffer, cls, member);
			
			string code = GenerateCodeFromMember (member);
			
			int line, col;
			buffer.GetLineColumnFromPosition (pos, out line, out col);
			
			string indent = GetLineIndent (buffer, line);
			code = Indent (code, indent, false);
			
			buffer.InsertText (pos, code);
			
			return FindGeneratedMember (ctx, buffer, cls, member);
		}
		
		protected CodeTypeReference ReturnTypeToDom (IReturnType rtype)
		{
			CodeTypeReference [] argTypes = null;
			CodeTypeReference typeRef = null;
			int i;
			
			ReturnTypeList genericArgs = rtype.GenericArguments;
			if (genericArgs != null && genericArgs.Count > 0) {
				argTypes = new CodeTypeReference [genericArgs.Count];
				for (i = 0; i < genericArgs.Count; i++)
					argTypes[i] = ReturnTypeToDom (genericArgs[i]);
			}
			
			if (argTypes != null)
				typeRef = new CodeTypeReference (rtype.FullyQualifiedName, argTypes);
			else
				typeRef = new CodeTypeReference (rtype.FullyQualifiedName);
			
			if (rtype.ArrayCount == 0)
				return typeRef;
			
			int [] dim = rtype.ArrayDimensions;
			for (i = 0; i < dim.Length; i++)
				typeRef = new CodeTypeReference (typeRef, dim[i]);
			
			return typeRef;
		}
		
		public virtual IMember ImplementMember (RefactorerContext ctx, IClass cls, string prefix, IMember member)
		{
			CodeTypeMember m;
			
			if (member is IEvent) {
				CodeMemberEvent mEvent = new CodeMemberEvent ();
				m = (CodeTypeMember) mEvent;
				
				mEvent.Type = ReturnTypeToDom (member.ReturnType);
			} else if (member is IMethod) {
				CodeMemberMethod mMethod = new CodeMemberMethod ();
				IMethod method = (IMethod) member;
				m = (CodeMemberMethod) mMethod;
				
				mMethod.ReturnType = ReturnTypeToDom (member.ReturnType);
				
				foreach (IParameter param in method.Parameters) {
					CodeParameterDeclarationExpression par;
					par = new CodeParameterDeclarationExpression (ReturnTypeToDom (param.ReturnType), param.Name);
					mMethod.Parameters.Add (par);
				}
			} else if (member is IProperty) {
				CodeMemberProperty mProperty = new CodeMemberProperty ();
				IProperty property = (IProperty) member;
				m = (CodeMemberProperty) mProperty;
				
				mProperty.HasGet = property.CanGet;
				mProperty.HasSet = property.CanSet;
				
				mProperty.Type = ReturnTypeToDom (member.ReturnType);
			} else {
				return null;
			}
			
			if (prefix != null)
				m.Name = prefix + member.Name;
			else
				m.Name = member.Name;
			
			m.Attributes = MemberAttributes.Public;
			
			return AddMember (ctx, cls, m);
		}
		
		public virtual void RemoveMember (RefactorerContext ctx, IClass cls, IMember member)
		{
			IEditableTextFile buffer = null;
			int pos = -1;
			
			for (int i = 0; i < cls.Parts.Length; i++) {
				if ((buffer = ctx.GetFile (cls.Parts[i].Region.FileName)) == null)
					continue;
				
				if ((pos = GetMemberNamePosition (buffer, member)) != -1)
					break;
			}
			
			if (pos == -1)
				return;
			
			IRegion reg = GetMemberBounds (buffer, member);
			int sp = buffer.GetPositionFromLineColumn (reg.BeginLine, reg.BeginColumn);
			int ep = buffer.GetPositionFromLineColumn (reg.EndLine, reg.EndColumn);
			buffer.DeleteText (sp, ep - sp);
		}
		
		public virtual IMember ReplaceMember (RefactorerContext ctx, IClass cls, IMember oldMember, CodeTypeMember memberInfo)
		{
			IEditableTextFile buffer = null;
			int pos = -1;
			
			for (int i = 0; i < cls.Parts.Length; i++) {
				if ((buffer = ctx.GetFile (cls.Parts[i].Region.FileName)) == null)
					continue;
				
				if ((pos = GetMemberNamePosition (buffer, oldMember)) != -1)
					break;
			}
			
			if (pos == -1)
				return null;
			
			IRegion reg = GetMemberBounds (buffer, oldMember);
			int sp = buffer.GetPositionFromLineColumn (reg.BeginLine, reg.BeginColumn);
			int ep = buffer.GetPositionFromLineColumn (reg.EndLine, reg.EndColumn);
			buffer.DeleteText (sp, ep - sp);
			
			string code = GenerateCodeFromMember (memberInfo);
			string indent = GetLineIndent (buffer, reg.BeginLine);
			code = Indent (code, indent, false);
			
			buffer.InsertText (sp, code);
			
			return FindGeneratedMember (ctx, buffer, cls, memberInfo);
		}
		
		public virtual IMember RenameMember (RefactorerContext ctx, IClass cls, IMember member, string newName)
		{
			IEditableTextFile file = null;
			int pos = -1;
			
			for (int i = 0; i < cls.Parts.Length; i++) {
				if ((file = ctx.GetFile (cls.Parts[i].Region.FileName)) == null)
					continue;
				
				if ((pos = GetMemberNamePosition (file, member)) != -1)
					break;
			}
			
			if (pos == -1)
				return null;
			
			string name;
			if (member is IMethod && ((IMethod) member).IsConstructor)
				name = cls.Name;
			else
				name = member.Name;
			
			string txt = file.GetText (pos, pos + name.Length);
			if (txt != name)
				return null;
			
			file.DeleteText (pos, txt.Length);
			file.InsertText (pos, newName);
			
			CodeTypeMember memberInfo;
			if (member is IField)
				memberInfo = new CodeMemberField ();
			else if (member is IMethod)
				memberInfo = new CodeMemberMethod ();
			else if (member is IProperty)
				memberInfo = new CodeMemberProperty ();
			else if (member is IEvent)
				memberInfo = new CodeMemberEvent ();
			else
				return null;
			
			memberInfo.Name = newName;
			return FindGeneratedMember (ctx, file, cls, memberInfo);
		}
		
		public virtual MemberReferenceCollection FindMemberReferences (RefactorerContext ctx, string fileName, IClass cls, IMember member)
		{
			if (member is IField)
				return FindFieldReferences (ctx, fileName, cls, (IField) member);
			else if (member is IMethod)
				return FindMethodReferences (ctx, fileName, cls, (IMethod) member);
			else if (member is IProperty)
				return FindPropertyReferences (ctx, fileName, cls, (IProperty) member);
			else if (member is IEvent)
				return FindEventReferences (ctx, fileName, cls, (IEvent) member);
			else
				return null;
		}
		
		///
		/// EncapsulateFieldImpGetSet:
		///
		/// Override this method for each language to fill-in the Get/SetStatements
		///
		protected virtual void EncapsulateFieldImpGetSet (RefactorerContext ctx, IClass cls, IField field, CodeMemberProperty  prop)
		{
			
		}
		
		public virtual IMember EncapsulateField (RefactorerContext ctx, IClass cls, IField field, string propName)
		{
			// If the field isn't already private/protected/internal, we'll need to fix it to be
			if (true || field.IsPublic || (!field.IsPrivate && !field.IsProtectedOrInternal)) {
				IEditableTextFile file = null;
				int pos = -1;
				
				// Find the file the field is contained in
				for (int i = 0; i < cls.Parts.Length; i++) {
					if ((file = ctx.GetFile (cls.Parts[i].Region.FileName)) == null)
						continue;
					
					if ((pos = GetMemberNamePosition (file, field)) != -1)
						break;
				}
				
				if (pos != -1) {
					// FIXME: need a way to get the CodeMemberField fieldInfo as a parsed object
					// (so we don't lose initialization state nor custom attributes, etc).
//					CodeMemberField fieldInfo = new CodeMemberField ();
//					
//					fieldInfo.Attributes = fieldInfo.Attributes & ~MemberAttributes.Public;
//					fieldInfo.Attributes |= MemberAttributes.Private;
//					
//					RemoveMember (ctx, cls, field);
//					AddMember (ctx, cls, fieldInfo);
					
					//int begin = file.GetPositionFromLineColumn (field.Region.BeginLine, field.Region.BeginColumn);
					//int end = file.GetPositionFromLineColumn (field.Region.EndLine, field.Region.EndColumn);
					//
					//string snippet = file.GetText (begin, end);
					//
					//Console.WriteLine ("field declaration: {0}", snippet);
					//
					//IRegion region = GetMemberBounds (file, field);
					//
					//begin = file.GetPositionFromLineColumn (region.BeginLine, region.BeginColumn);
					//end = file.GetPositionFromLineColumn (region.EndLine, region.EndColumn);
					//
					//snippet = file.GetText (begin, end);
					//
					//Console.WriteLine ("delete '{0}'", snippet);
				}
			}
			
			CodeMemberProperty prop = new CodeMemberProperty ();
			prop.Name = propName;
			
			prop.Type = ReturnTypeToDom (field.ReturnType);
			prop.Attributes = MemberAttributes.Public | MemberAttributes.Final;
			if (field.IsStatic)
				prop.Attributes |= MemberAttributes.Static;
			
			prop.HasGet = true;
			
			// if the field was public before, then we provide a 'set', else we don't
			prop.HasSet = (field.IsPublic || (!field.IsPrivate && !field.IsProtectedOrInternal));
			
			EncapsulateFieldImpGetSet (ctx, cls, field, prop);
			
			return AddMember (ctx, cls, prop);
		}
		

		/// Method overridables ////////////////////////////
		
		protected virtual IMethod RenameMethod (RefactorerContext ctx, IClass cls, IMethod method, string newName)
		{
			return null;
		}
		
		protected virtual MemberReferenceCollection FindMethodReferences (RefactorerContext ctx, string fileName, IClass cls, IMethod method)
		{
			return null;
		}
		

		/// Field overridables ////////////////////////////
		
		protected virtual IField RenameField (RefactorerContext ctx, IClass cls, IField field, string newName)
		{
			return null;
		}
		
		protected virtual MemberReferenceCollection FindFieldReferences (RefactorerContext ctx, string fileName, IClass cls, IField field)
		{
			return null;
		}


		/// Property overridables ////////////////////////////
		
		protected virtual IProperty RenameProperty (RefactorerContext ctx, IClass cls, IProperty property, string newName)
		{
			return null;
		}
		
		protected virtual MemberReferenceCollection FindPropertyReferences (RefactorerContext ctx, string fileName, IClass cls, IProperty property)
		{
			return null;
		}

		/// Event overridables ////////////////////////////		
		
		protected virtual IEvent RenameEvent (RefactorerContext ctx, IClass cls, IEvent evnt, string newName)
		{
			return null;
		}
		
		protected virtual MemberReferenceCollection FindEventReferences (RefactorerContext ctx, string fileName, IClass cls, IEvent evnt)
		{
			return null;
		}


		/// LocalVariable overridables /////////////////////
		
		public virtual bool RenameVariable (RefactorerContext ctx, LocalVariable var, string newName)
		{
			IEditableTextFile file = ctx.GetFile (var.Region.FileName);
			if (file == null)
				return false;
			
			int pos = GetVariableNamePosition (file, var);
			if (pos == -1)
				return false;
			
			string txt = file.GetText (pos, pos + var.Name.Length);
			if (txt != var.Name)
				return false;
			
			file.DeleteText (pos, txt.Length);
			file.InsertText (pos, newName);
			
			ctx.ParserContext.ParserDatabase.UpdateFile (file.Name, file.Text);
			
			return true;
		}

		public virtual MemberReferenceCollection FindVariableReferences (RefactorerContext ctx, string fileName, LocalVariable var)
		{
			return null;
		}


		/// Parameter overridables /////////////////////
		
		public virtual bool RenameParameter (RefactorerContext ctx, IParameter param, string newName)
		{
			IMember member = param.DeclaringMember;
			IEditableTextFile file = null;
			int pos = -1;
			
			// It'd be nice if we didn't have to worry about this being null
			if (member.Region.FileName != null) {
				if ((file = ctx.GetFile (member.Region.FileName)) != null)
					pos = GetParameterNamePosition (file, param);
			}
			
			// Plan B. - fallback to searching all partial class files for this parameter's parent member
			if (pos == -1) {
				IClass cls = member.DeclaringType;
				
				for (int i = 0; i < cls.Parts.Length; i++) {
					if ((file = ctx.GetFile (cls.Parts[i].Region.FileName)) == null)
						continue;
					
					// sanity check, if the parent member isn't here then neither is the param
					//if ((pos = GetMemberNamePosition (file, member)) == -1)
					//	continue;
					
					if ((pos = GetParameterNamePosition (file, param)) != -1)
						break;
				}
				
				if (pos == -1)
					return false;
			}
			
			string txt = file.GetText (pos, pos + param.Name.Length);
			if (txt != param.Name)
				return false;
			
			file.DeleteText (pos, txt.Length);
			file.InsertText (pos, newName);
			
			ctx.ParserContext.ParserDatabase.UpdateFile (file.Name, file.Text);
			
			return true;
		}

		public virtual MemberReferenceCollection FindParameterReferences (RefactorerContext ctx, string fileName, IParameter param)
		{
			return null;
		}

		/// Helper overridables ////////////////////////////

		protected virtual int GetMemberNamePosition (IEditableTextFile file, IMember member)
		{
			return -1;
		}

		protected virtual int GetVariableNamePosition (IEditableTextFile file, LocalVariable var)
		{
			return -1;
		}
		
		protected virtual int GetParameterNamePosition (IEditableTextFile file, IParameter param)
		{
			return -1;
		}

		protected virtual IRegion GetMemberBounds (IEditableTextFile file, IMember member)
		{
			int minLin = member.Region.BeginLine;
			int minCol = member.Region.BeginColumn;
			int maxLin = member.Region.EndLine;
			int maxCol = member.Region.EndColumn;
			
			foreach (IAttributeSection att in member.Attributes) {
				if (att.Region.BeginLine < minLin) {
					minLin = att.Region.BeginLine;
					minCol = att.Region.BeginColumn;
				} else if (att.Region.BeginLine == minLin && att.Region.BeginColumn < minCol) {
					minCol = att.Region.BeginColumn;
				}
				
				if (att.Region.EndLine > maxLin) {
					maxLin = att.Region.EndLine;
					maxCol = att.Region.EndColumn;
				} else if (att.Region.EndLine == maxLin && att.Region.EndColumn > maxCol) {
					maxCol = att.Region.EndColumn;
				}
			}
			return new DefaultRegion (minLin, minCol, maxLin, maxCol);
		}
		
		protected virtual string GenerateCodeFromMember (CodeTypeMember member)
		{
			CodeTypeDeclaration type = new CodeTypeDeclaration ("temp");
			type.Members.Add (member);
			ICodeGenerator gen = GetGenerator ();
			StringWriter sw = new StringWriter ();
			gen.GenerateCodeFromType (type, sw, GetOptions (member is CodeMemberMethod));
			string code = sw.ToString ();
			int i = code.IndexOf ('{');
			int j = code.LastIndexOf ('}');
			code = code.Substring (i+1, j-i-1);
			if (member is CodeMemberMethod)
				if ((i = code.IndexOf ('(')) != -1)
					code = code.Insert (i, " ");
			return RemoveIndent (code);
		}
		

		/// Helper methods ////////////////////////////

		// Returns a reparsed IClass instance that contains the generated code.
		protected IClass GetGeneratedClass (RefactorerContext ctx, IEditableTextFile buffer, IClass cls)
		{
			IParseInformation pi = ctx.ParserContext.ParserDatabase.UpdateFile (buffer.Name, buffer.Text);
			foreach (IClass rclass in ((ICompilationUnit) pi.MostRecentCompilationUnit).Classes) {
				if (cls.Name == rclass.Name)
					return rclass;
			}
			return null;
		}
		
		protected IMember FindGeneratedMember (RefactorerContext ctx, IEditableTextFile buffer, IClass cls, CodeTypeMember member)
		{
			IClass rclass = GetGeneratedClass (ctx, buffer, cls);
			if (rclass != null) {
				if (member is CodeMemberField) {
					foreach (IField m in rclass.Fields)
						if (m.Name == member.Name)
							return m;
				} else if (member is CodeMemberProperty) {
					foreach (IProperty m in rclass.Properties)
						if (m.Name == member.Name)
							return m;
				} else if (member is CodeMemberEvent) {
					foreach (IEvent m in rclass.Events)
						if (m.Name == member.Name)
							return m;
				} else if (member is CodeMemberMethod) {
					foreach (IMethod m in rclass.Methods)
						if (m.Name == member.Name)
							return m;
				}
			}
			return null;
		}
		
		protected string RemoveIndent (string code)
		{
			string[] lines = code.Split ('\n');
			int minInd = int.MaxValue;
			
			for (int n=0; n<lines.Length; n++) {
				string line = lines [n];
				for (int i=0; i<line.Length; i++) {
					char c = line [i];
					if (c != ' ' && c != '\t') {
						if (i < minInd)
							minInd = i;
						break;
					}
				}
			}
			
			if (minInd == int.MaxValue)
				minInd = 0;
			
			int firstLine = -1, lastLine = -1;
			
			for (int n=0; n<lines.Length; n++) {
				if (minInd >= lines[n].Length)
					continue;
					
				if (lines[n].Trim (' ','\t') != "") {
					if (firstLine == -1)
						firstLine = n;
					lastLine = n;
				}
				
				lines [n] = lines [n].Substring (minInd);
			}
			
			if (firstLine == -1)
				return "";
			
			return string.Join ("\n", lines, firstLine, lastLine - firstLine + 1);
		}
		
		protected string Indent (string code, string indent, bool indentFirstLine)
		{
			code = code.Replace ("\n", "\n" + indent);
			if (indentFirstLine)
				return indent + code;
			else
				return code;
		}
		
		protected virtual int GetNewMemberPosition (IEditableTextFile buffer, IClass cls, CodeTypeMember member)
		{
			if (member is CodeMemberField)
				return GetNewFieldPosition (buffer, cls);
			else if (member is CodeMemberMethod)
				return GetNewMethodPosition (buffer, cls);
			else if (member is CodeMemberEvent)
				return GetNewEventPosition (buffer, cls);
			else if (member is CodeMemberProperty)
				return GetNewPropertyPosition (buffer, cls);
			else
				throw new InvalidOperationException ("Invalid member type: " + member);
		}
		
		protected virtual int GetNewFieldPosition (IEditableTextFile buffer, IClass cls)
		{
			if (cls.Fields.Count == 0) {
				int sp = buffer.GetPositionFromLineColumn (cls.BodyRegion.BeginLine, cls.BodyRegion.BeginColumn);
				int ep = buffer.GetPositionFromLineColumn (cls.BodyRegion.EndLine, cls.BodyRegion.EndColumn);
				string s = buffer.GetText (sp, ep);
				int i = s.IndexOf ('{');
				if (i == -1) return -1;
				i++;
				int pos = GetNextLine (buffer, sp + i);
				string ind = GetLineIndent (buffer, cls.BodyRegion.BeginLine);
				buffer.InsertText (pos, ind + "\t\n");
				return pos + ind.Length + 1;
			} else {
				IField f = cls.Fields [cls.Fields.Count - 1];
				int pos = buffer.GetPositionFromLineColumn (f.Region.EndLine, f.Region.EndColumn);
				pos = GetNextLine (buffer, pos);
				string ind = GetLineIndent (buffer, f.Region.EndLine);
				buffer.InsertText (pos, ind);
				return pos + ind.Length;
			}
		}
		
		protected virtual int GetNewMethodPosition (IEditableTextFile buffer, IClass cls)
		{
			if (cls.Methods.Count == 0) {
				int pos = GetNewPropertyPosition (buffer, cls);
				int line, col;
				buffer.GetLineColumnFromPosition (pos, out line, out col);
				string ind = GetLineIndent (buffer, line);
				pos = GetNextLine (buffer, pos);
				buffer.InsertText (pos, ind);
				return pos + ind.Length;
			} else {
				IMethod m = cls.Methods [cls.Methods.Count - 1];
				
				int pos;
				if (m.BodyRegion != null && m.BodyRegion.EndLine > 0) {
					pos = buffer.GetPositionFromLineColumn (m.BodyRegion.EndLine, m.BodyRegion.EndColumn);
				} else {
					// Abstract or P/Inboke methods don't have a body
					pos = buffer.GetPositionFromLineColumn (m.Region.EndLine, m.Region.EndColumn);
				}
				
				pos = GetNextLine (buffer, pos);
				pos = GetNextLine (buffer, pos);
				string ind = GetLineIndent (buffer, m.Region.EndLine);
				buffer.InsertText (pos, ind);
				return pos + ind.Length;
			}
		}
		
		protected virtual int GetNewPropertyPosition (IEditableTextFile buffer, IClass cls)
		{
			if (cls.Properties.Count == 0) {
				int pos = GetNewFieldPosition (buffer, cls);
				int line, col;
				buffer.GetLineColumnFromPosition (pos, out line, out col);
				string indent = GetLineIndent (buffer, line);
				pos = GetNextLine (buffer, pos);
				buffer.InsertText (pos, indent);
				return pos + indent.Length;
			} else {
				IProperty m = cls.Properties [cls.Properties.Count - 1];
				int pos = buffer.GetPositionFromLineColumn (m.BodyRegion.EndLine, m.BodyRegion.EndColumn);
				pos = GetNextLine (buffer, pos);
				pos = GetNextLine (buffer, pos);
				string indent = GetLineIndent (buffer, m.Region.EndLine);
				buffer.InsertText (pos, indent);
				return pos + indent.Length;
			}
		}
		
		protected virtual int GetNewEventPosition (IEditableTextFile buffer, IClass cls)
		{
			if (cls.Events.Count == 0) {
				int pos = GetNewMethodPosition (buffer, cls);
				int line, col;
				buffer.GetLineColumnFromPosition (pos, out line, out col);
				string ind = GetLineIndent (buffer, line);
				pos = GetNextLine (buffer, pos);
				buffer.InsertText (pos, ind);
				return pos + ind.Length;
			} else {
				IEvent m = cls.Events [cls.Events.Count - 1];
				int pos = buffer.GetPositionFromLineColumn (m.BodyRegion.EndLine, m.BodyRegion.EndColumn);
				pos = GetNextLine (buffer, pos);
				pos = GetNextLine (buffer, pos);
				string ind = GetLineIndent (buffer, m.Region.EndLine);
				buffer.InsertText (pos, ind);
				return pos + ind.Length;
			}
		}
		
		protected virtual int GetNextLine (IEditableTextFile buffer, int pos)
		{
			while (pos < buffer.Length) {
				string s = buffer.GetText (pos, pos + 1);
				if (s == "\n") {
					buffer.InsertText (pos + 1, "\n");
					return pos + 1;
				}
				if (s != " " && s == "\t") {
					buffer.InsertText (pos, "\n\n");
					return pos + 1;
				}
				pos++;
			}
			return pos;
		}
		
		protected string GetLineIndent (IEditableTextFile buffer, int line)
		{
			int pos = buffer.GetPositionFromLineColumn (line, 1);
			int ipos = pos;
			string s = buffer.GetText (pos, pos + 1);
			while ((s == " " || s == "\t") && pos < buffer.Length) {
				pos++;
				s = buffer.GetText (pos, pos + 1);
			}
			return buffer.GetText (ipos, pos);
		}
		
		protected virtual CodeGeneratorOptions GetOptions (bool isMethod)
		{
			CodeGeneratorOptions ops = new CodeGeneratorOptions ();
			ops.IndentString = "\t";
			if (isMethod)
				ops.BracingStyle = "C";
			return ops;
		}
	}
}
