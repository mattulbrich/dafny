//-----------------------------------------------------------------------------
//
// Copyright (C) Microsoft Corporation.  All Rights Reserved.
//
//-----------------------------------------------------------------------------
using System;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Numerics;
using Microsoft.Boogie;

namespace Microsoft.Dafny {
  public class Program {
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Name != null);
      Contract.Invariant(DefaultModule != null);
    }

    public readonly string Name;
    public List<ModuleDefinition/*!*/>/*!*/ Modules; // filled in during resolution.
                                                     // Resolution essentially flattens the module hierarchy, for
                                                     // purposes of translation and compilation.
    public List<ModuleDefinition> CompileModules; // filled in during resolution.
                                                  // Contains the definitions to be used for compilation.
                                    
    public readonly ModuleDecl DefaultModule;
    public readonly ModuleDefinition DefaultModuleDef;
    public readonly BuiltIns BuiltIns;
    public readonly List<TranslationTask> TranslationTasks;
    public Program(string name, [Captured] ModuleDecl module, [Captured] BuiltIns builtIns) {
      Contract.Requires(name != null);
      Contract.Requires(module != null);
      Contract.Requires(module is LiteralModuleDecl);
      Name = name;
      DefaultModule = module;
      DefaultModuleDef = (DefaultModuleDecl)((LiteralModuleDecl)module).ModuleDef;
      BuiltIns = builtIns;
      Modules = new List<ModuleDefinition>();
      CompileModules = new List<ModuleDefinition>();
      TranslationTasks = new List<TranslationTask>();
    }
  }

  public class BuiltIns
  {
    public readonly ModuleDefinition SystemModule = new ModuleDefinition(Token.NoToken, "_System", false, false, null, null, true);
    Dictionary<int, ClassDecl/*!*/> arrayTypeDecls = new Dictionary<int, ClassDecl>();
    public readonly ClassDecl ObjectDecl;
    public BuiltIns() {
      // create class 'object'
      ObjectDecl = new ClassDecl(Token.NoToken, "object", SystemModule, new List<TypeParameter>(), new List<MemberDecl>(), null);
      SystemModule.TopLevelDecls.Add(ObjectDecl);
      // add one-dimensional arrays, since they may arise during type checking
      UserDefinedType tmp = ArrayType(Token.NoToken, 1, Type.Int, true);
    }

    public UserDefinedType ArrayType(int dims, Type arg) {
      return ArrayType(Token.NoToken, dims, arg, false);
    }
    public UserDefinedType ArrayType(IToken tok, int dims, Type arg, bool allowCreationOfNewClass) {
      Contract.Requires(tok != null);
      Contract.Requires(1 <= dims);
      Contract.Requires(arg != null);
      Contract.Ensures(Contract.Result<UserDefinedType>() != null);

      List<Type/*!*/> typeArgs = new List<Type/*!*/>();
      typeArgs.Add(arg);
      UserDefinedType udt = new UserDefinedType(tok, ArrayClassName(dims), typeArgs, null);
      if (allowCreationOfNewClass && !arrayTypeDecls.ContainsKey(dims)) {
        ArrayClassDecl arrayClass = new ArrayClassDecl(dims, SystemModule);
        for (int d = 0; d < dims; d++) {
          string name = dims == 1 ? "Length" : "Length" + d;
          string compiledName = dims == 1 ? "Length" : "GetLength(" + d + ")";
          Field len = new SpecialField(Token.NoToken, name, compiledName, "new BigInteger(", ")", false, false, false, Type.Int, null);
          len.EnclosingClass = arrayClass;  // resolve here
          arrayClass.Members.Add(len);
        }
        arrayTypeDecls.Add(dims, arrayClass);
        SystemModule.TopLevelDecls.Add(arrayClass);
      }
      udt.ResolvedClass = arrayTypeDecls[dims];
      return udt;
    }

    public static string ArrayClassName(int dims) {
      Contract.Requires(1 <= dims);
      if (dims == 1) {
        return "array";
      } else {
        return "array" + dims;
      }
    }
  }

  public class Attributes {
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Name != null);
      Contract.Invariant(cce.NonNullElements(Args));
    }

    public readonly string Name;
    /*Frozen*/
    public readonly List<Argument/*!*/>/*!*/ Args;
    public readonly Attributes Prev;

    public Attributes(string name, [Captured] List<Argument/*!*/>/*!*/ args, Attributes prev) {
      Contract.Requires(name != null);
      Contract.Requires(cce.NonNullElements(args));
      Name = name;
      Args = args;
      Prev = prev;
    }

    public static bool Contains(Attributes attrs, string nm) {
      Contract.Requires(nm != null);
      for (; attrs != null; attrs = attrs.Prev) {
        if (attrs.Name == nm) {
          return true;
        }
      }
      return false;
    }

    /// <summary>
    /// Returns true if "nm" is a specified attribute.  If it is, then:
    /// - if the attribute is {:nm true}, then value==true
    /// - if the attribute is {:nm false}, then value==false
    /// - if the attribute is anything else, then value returns as whatever it was passed in as.
    /// </summary>
    public static bool ContainsBool(Attributes attrs, string nm, ref bool value) {
      Contract.Requires(nm != null);
      for (; attrs != null; attrs = attrs.Prev) {
        if (attrs.Name == nm) {
          if (attrs.Args.Count == 1) {
            var arg = attrs.Args[0].E as LiteralExpr;
            if (arg != null && arg.Value is bool) {
              value = (bool)arg.Value;
            }
          }
          return true;
        }
      }
      return false;
    }

    public class Argument
    {
      public readonly IToken Tok;
      public readonly string S;
      public readonly Expression E;
      [ContractInvariantMethod]
      void ObjectInvariant() {
        Contract.Invariant(Tok != null);
        Contract.Invariant((S == null) != (E == null));
      }

      public Argument(IToken tok, string s) {
        Contract.Requires(tok != null);
        Contract.Requires(s != null);
        Tok = tok;
        S = s;
      }
      public Argument(IToken tok, Expression e) {
        Contract.Requires(tok != null);
        Contract.Requires(e != null);
        Tok = tok;
        E = e;
      }
    }
  }

  // ------------------------------------------------------------------------------------------------------

  public abstract class Type {
    public static readonly BoolType Bool = new BoolType();
    public static readonly IntType Int = new IntType();
    /// <summary>
    /// Used in error situations in order to reduce further error messages.
    /// </summary>
    //[Pure(false)]
    public static Type Flexible {
      get {
        Contract.Ensures(Contract.Result<Type>() != null);
        return new InferredTypeProxy();
      }
    }

    [Pure]
    public abstract string TypeName(ModuleDefinition/*?*/ context);
    [Pure]
    public override string ToString() {
      return TypeName(null);
    }

    /// <summary>
    /// Return the most constrained version of "this".
    /// </summary>
    /// <returns></returns>
    public Type Normalize() {
      Contract.Ensures(Contract.Result<Type>() != null);
      Type type = this;
      while (true) {
        TypeProxy pt = type as TypeProxy;
        if (pt != null && pt.T != null) {
          type = pt.T;
        } else {
          return type;
        }
      }
    }

    public bool IsSubrangeType {
      get { return this is NatType; }
    }

    public bool IsRefType {
      get {
        if (this is ObjectType) {
          return true;
        } else {
          UserDefinedType udt = this as UserDefinedType;
          return udt != null && udt.ResolvedParam == null && udt.ResolvedClass is ClassDecl;
        }
      }
    }
    public bool IsArrayType {
      get {
        return AsArrayType != null;
      }
    }
    public ArrayClassDecl/*?*/ AsArrayType {
      get {
        UserDefinedType udt = UserDefinedType.DenotesClass(this);
        return udt == null ? null : udt.ResolvedClass as ArrayClassDecl;
      }
    }
    public bool IsDatatype {
      get {
        return AsDatatype != null;
      }
    }
    public DatatypeDecl AsDatatype {
      get {
        UserDefinedType udt = this as UserDefinedType;
        if (udt == null) {
          return null;
        } else {
          return udt.ResolvedClass as DatatypeDecl;
        }
      }
    }
    public bool IsIndDatatype {
      get {
        return AsIndDatatype != null;
      }
    }
    public IndDatatypeDecl AsIndDatatype {
      get {
        UserDefinedType udt = this as UserDefinedType;
        if (udt == null) {
          return null;
        } else {
          return udt.ResolvedClass as IndDatatypeDecl;
        }
      }
    }
    public bool IsCoDatatype {
      get {
        return AsCoDatatype != null;
      }
    }
    public CoDatatypeDecl AsCoDatatype {
      get {
        UserDefinedType udt = this as UserDefinedType;
        if (udt == null) {
          return null;
        } else {
          return udt.ResolvedClass as CoDatatypeDecl;
        }
      }
    }
    public bool InvolvesCoDatatype {
      get {
        return IsCoDatatype;  // TODO: should really check structure of the type recursively
      }
    }
    public bool IsTypeParameter {
      get {
        return AsTypeParameter != null;
      }
    }
    public TypeParameter AsTypeParameter {
      get {
        UserDefinedType ct = this as UserDefinedType;
        return ct == null ? null : ct.ResolvedParam;
      }
    }
    public virtual bool SupportsEquality {
      get {
        return true;
      }
    }
  }

  /// <summary>
  /// A NonProxy type is a fully constrained type.  It may contain members.
  /// </summary>
  public abstract class NonProxyType : Type
  {
  }

  public abstract class BasicType : NonProxyType
  {
  }

  public class BoolType : BasicType {
    [Pure]
    public override string TypeName(ModuleDefinition context) {
      return "bool";
    }
  }

  public class IntType : BasicType {
    [Pure]
    public override string TypeName(ModuleDefinition context) {
      return "int";
    }
  }

  public class NatType : IntType
  {
    [Pure]
    public override string TypeName(ModuleDefinition context) {
      return "nat";
    }
  }

  public class ObjectType : BasicType
  {
    [Pure]
    public override string TypeName(ModuleDefinition context) {
      return "object";
    }
  }

  public abstract class CollectionType : NonProxyType
  {
    public readonly Type Arg;  // denotes the Domain type for a Map
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Arg != null);
    }
    public CollectionType(Type arg) {
      Contract.Requires(arg != null);
      this.Arg = arg;
    }
    public override bool SupportsEquality {
      get {
        return Arg.SupportsEquality;
      }
    }
  }

  public class SetType : CollectionType {
    public SetType(Type arg) : base(arg) {
      Contract.Requires(arg != null);
    }
    [Pure]
    public override string TypeName(ModuleDefinition context) {
      Contract.Ensures(Contract.Result<string>() != null);
      Contract.Assume(cce.IsPeerConsistent(Arg));
      return "set<" + base.Arg.TypeName(context) + ">";
    }
  }

  public class MultiSetType : CollectionType
  {
    public MultiSetType(Type arg) : base(arg) {
      Contract.Requires(arg != null);
    }
    [Pure]
    public override string TypeName(ModuleDefinition context) {
      Contract.Ensures(Contract.Result<string>() != null);
      Contract.Assume(cce.IsPeerConsistent(Arg));
      return "multiset<" + base.Arg.TypeName(context) + ">";
    }
  }

  public class SeqType : CollectionType {
    public SeqType(Type arg) : base(arg) {
      Contract.Requires(arg != null);

    }
    [Pure]
    public override string TypeName(ModuleDefinition context) {
      Contract.Ensures(Contract.Result<string>() != null);
      Contract.Assume(cce.IsPeerConsistent(Arg));
      return "seq<" + base.Arg.TypeName(context) + ">";
    }
  }
  public class MapType : CollectionType
  {
    public Type Range;
    public MapType(Type domain, Type range) : base(domain) {
      Contract.Requires(domain != null && range != null);
      Range = range;
    }
    public Type Domain {
      get { return Arg; }
    }
    [Pure]
    public override string TypeName(ModuleDefinition context) {
      Contract.Ensures(Contract.Result<string>() != null);
      Contract.Assume(cce.IsPeerConsistent(Domain));
      Contract.Assume(cce.IsPeerConsistent(Range));
      return "map<" + Domain.TypeName(context) + ", " + Range.TypeName(context) + ">";
    }
  }

  public class UserDefinedType : NonProxyType
  {
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(tok != null);
      Contract.Invariant(Name != null);
      Contract.Invariant(cce.NonNullElements(TypeArgs));
      Contract.Invariant(cce.NonNullElements(Path));
    }

    public readonly List<IToken> Path;  // may be null
    public readonly IToken tok;  // token of the Name
    public readonly string Name;
    [Rep]
    public readonly List<Type/*!*/>/*!*/ TypeArgs;

    public string FullName {
      get {
        if (ResolvedClass != null && !ResolvedClass.Module.IsDefaultModule) {
          return ResolvedClass.Module.Name + "." + Name;
        } else {
          return Name;
        }
      }
    }

    string compileName;
    public string CompileName {
      get {
        if (compileName == null) {
          compileName = NonglobalVariable.CompilerizeName(Name);
        }
        return compileName;
      }
    }
    public string FullCompileName {
      get {
        if (ResolvedClass != null && !ResolvedClass.Module.IsDefaultModule) {
          return ResolvedClass.Module.CompileName + "." + CompileName;
        } else {
          return CompileName;
        }
      }
    }

    public TopLevelDecl ResolvedClass;  // filled in by resolution, if Name denotes a class/datatype/iterator and TypeArgs match the type parameters of that class/datatype/iterator
    public TypeParameter ResolvedParam;  // filled in by resolution, if Name denotes an enclosing type parameter and TypeArgs is the empty list

    public UserDefinedType(IToken/*!*/ tok, string/*!*/ name, [Captured] List<Type/*!*/>/*!*/ typeArgs, List<IToken> moduleName) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(cce.NonNullElements(typeArgs));
      Contract.Requires(moduleName == null || cce.NonNullElements(moduleName));
      if (moduleName != null) this.Path = moduleName;
      else this.Path = new List<IToken>();
      this.tok = tok;
      this.Name = name;
      this.TypeArgs = typeArgs;
    }

    /// <summary>
    /// This constructor constructs a resolved class/datatype/iterator type
    /// </summary>
    public UserDefinedType(IToken/*!*/ tok, string/*!*/ name, TopLevelDecl/*!*/ cd, [Captured] List<Type/*!*/>/*!*/ typeArgs) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(cd != null);
      Contract.Requires(cce.NonNullElements(typeArgs));
      this.tok = tok;
      this.Name = name;
      this.TypeArgs = typeArgs;
      this.ResolvedClass = cd;
      this.Path = new List<IToken>();
    }

    /// <summary>
    /// This constructor constructs a resolved type parameter
    /// </summary>
    public UserDefinedType(IToken/*!*/ tok, string/*!*/ name, TypeParameter/*!*/ tp) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(tp != null);
      this.tok = tok;
      this.Name = name;
      this.TypeArgs = new List<Type/*!*/>();
      this.ResolvedParam = tp;
      this.Path = new List<IToken>();
    }

    /// <summary>
    /// If type denotes a resolved class type, then return that class type.
    /// Otherwise, return null.
    /// </summary>
    public static UserDefinedType DenotesClass(Type/*!*/ type) {
      Contract.Requires(type != null);
      Contract.Ensures(Contract.Result<UserDefinedType>() == null || Contract.Result<UserDefinedType>().ResolvedClass is ClassDecl);
      type = type.Normalize();
      UserDefinedType ct = type as UserDefinedType;
      if (ct != null && ct.ResolvedClass is ClassDecl) {
        return ct;
      } else {
        return null;
      }
    }

    public static Type ArrayElementType(Type type) {
      Contract.Requires(type.IsArrayType);

      Contract.Requires(type != null);
      Contract.Ensures(Contract.Result<Type>() != null);

      UserDefinedType udt = DenotesClass(type);
      Contract.Assert(udt != null);
      Contract.Assert(udt.TypeArgs.Count == 1);  // holds true of all array types
      return udt.TypeArgs[0];
    }

    [Pure]
    public override string TypeName(ModuleDefinition context) {
      Contract.Ensures(Contract.Result<string>() != null);
      string s = "";
      foreach (var t in Path) {
        if (context != null && t == context.tok) {
          // drop the prefix up to here
          s = "";
        } else {
          s += t.val + ".";
        }
      }
      s += Name;
      if (TypeArgs.Count != 0) {
        s += "<" + Util.Comma(",", TypeArgs, ty => ty.TypeName(context)) + ">";
      }
      return s;
    }

    public override bool SupportsEquality {
      get {
        if (ResolvedClass is ClassDecl) {
          return true;
        } else if (ResolvedClass is CoDatatypeDecl) {
          return false;
        } else if (ResolvedClass is IndDatatypeDecl) {
          var dt = (IndDatatypeDecl)ResolvedClass;
          Contract.Assume(dt.EqualitySupport != IndDatatypeDecl.ES.NotYetComputed);
          if (dt.EqualitySupport == IndDatatypeDecl.ES.Never) {
            return false;
          }
          Contract.Assert(dt.TypeArgs.Count == TypeArgs.Count);
          var i = 0;
          foreach (var tp in dt.TypeArgs) {
            if (tp.NecessaryForEqualitySupportOfSurroundingInductiveDatatype && !TypeArgs[i].SupportsEquality) {
              return false;
            }
            i++;
          }
          return true;
        } else if (ResolvedParam != null) {
          return ResolvedParam.MustSupportEquality;
        }
        Contract.Assume(false);  // the SupportsEquality getter requires the Type to have been successfully resolved
        return true;
      }
    }
  }

  public abstract class TypeProxy : Type {
    public Type T;  // filled in during resolution
    internal TypeProxy() {
    }

    [Pure]
    public override string TypeName(ModuleDefinition context) {
      Contract.Ensures(Contract.Result<string>() != null);

      Contract.Assume(T == null || cce.IsPeerConsistent(T));
      return T == null ? "?" : T.TypeName(context);
    }
    public override bool SupportsEquality {
      get {
        if (T != null) {
          return T.SupportsEquality;
        } else {
          return base.SupportsEquality;
        }
      }
    }
  }

  public abstract class UnrestrictedTypeProxy : TypeProxy {
  }

  /// <summary>
  /// This proxy stands for any type.
  /// </summary>
  public class InferredTypeProxy : UnrestrictedTypeProxy {
  }

  /// <summary>
  /// This proxy stands for any type, but it originates from an instantiated type parameter.
  /// </summary>
  public class ParamTypeProxy : UnrestrictedTypeProxy {
    public TypeParameter orig;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(orig != null);
    }

    public ParamTypeProxy(TypeParameter orig) {
      Contract.Requires(orig != null);
      this.orig = orig;
    }
  }

  public abstract class RestrictedTypeProxy : TypeProxy {
    /// <summary>
    /// The OrderID is used to simplify the unification code.  Each restricted type proxy should use its
    /// own OrderID.
    /// </summary>
    public abstract int OrderID {
      get;
    }
  }

  /// <summary>
  /// This proxy stands for any datatype.
  /// </summary>
  public class DatatypeProxy : RestrictedTypeProxy {
    public override int OrderID {
      get {
        return 0;
      }
    }
  }

  /// <summary>
  /// This proxy stands for object or any class/array type.
  /// </summary>
  public class ObjectTypeProxy : RestrictedTypeProxy {
    public override int OrderID {
      get {
        return 1;
      }
    }
  }

  /// <summary>
  /// This proxy stands for:
  ///     set(Arg) or seq(Arg) or map(Arg, Range)
  /// </summary>
  public class CollectionTypeProxy : RestrictedTypeProxy {
    public readonly Type Arg;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Arg != null);
    }

    public CollectionTypeProxy(Type arg) {
      Contract.Requires(arg != null);
      Arg = arg;
    }
    public override int OrderID {
      get {
        return 2;
      }
    }
  }

  /// <summary>
  /// This proxy stands for either:
  ///     int or set or multiset or seq
  /// if AllowSeq, or:
  ///     int or set or multiset
  /// if !AllowSeq.
  /// </summary>
  public class OperationTypeProxy : RestrictedTypeProxy {
    public readonly bool AllowSeq;
    public OperationTypeProxy(bool allowSeq) {
      AllowSeq = allowSeq;
    }
    public override int OrderID {
      get {
        return 3;
      }
    }
  }

  /// <summary>
  /// This proxy stands for:
  ///     seq(Arg) or array(Arg) or map(Arg, Range)
  /// </summary>
  public class IndexableTypeProxy : RestrictedTypeProxy {
    public readonly Type Arg, Domain;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Arg != null);
    }

    public IndexableTypeProxy(Type arg, Type domain) {
      Contract.Requires(arg != null);
      Arg = arg;
      Domain = domain;
    }
    public override int OrderID {
      get {
        return 4;
      }
    }
  }

  // ------------------------------------------------------------------------------------------------------

  public abstract class Declaration {
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(tok != null);
      Contract.Invariant(Name != null);
    }

    public IToken/*!*/ tok;
    public IToken BodyStartTok = Token.NoToken;
    public IToken BodyEndTok = Token.NoToken;
    public readonly string/*!*/ Name;
    string compileName;
    public virtual string CompileName {
      get {
        if (compileName == null) {
          compileName = NonglobalVariable.CompilerizeName(Name);
        }
        return compileName;
      }
    }
    public readonly Attributes Attributes;

    public Declaration(IToken tok, string name, Attributes attributes) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      this.tok = tok;
      this.Name = name;
      this.Attributes = attributes;
    }

    [Pure]
    public override string ToString() {
      Contract.Ensures(Contract.Result<string>() != null);
      return Name;
    }
  }

  public class TypeParameter : Declaration {
    public interface ParentType {
    }
    [Peer]
    ParentType parent;
    public ParentType Parent {
      get {
        return parent;
      }
      [param: Captured]
      set {
        Contract.Requires(Parent == null);  // set it only once
        Contract.Requires(value != null);
        // BUGBUG:  The following line is a workaround to tell the verifier that 'value' is not of an Immutable type.
        // A proper solution would be to be able to express that in the program (in a specification or attribute) or
        // to be able to declare 'parent' as [PeerOrImmutable].
        Contract.Requires(value is TopLevelDecl || value is Function || value is Method || value is DatatypeCtor);
        //modifies parent;
        parent = value;
      }
    }
    public enum EqualitySupportValue { Required, InferredRequired, Unspecified }
    public EqualitySupportValue EqualitySupport;  // the resolver may change this value from Unspecified to InferredRequired (for some signatures that may immediately imply that equality support is required)
    public bool MustSupportEquality {
      get { return EqualitySupport != EqualitySupportValue.Unspecified; }
    }

    public bool NecessaryForEqualitySupportOfSurroundingInductiveDatatype = false;  // computed during resolution; relevant only when Parent denotes an IndDatatypeDecl

    public bool IsAbstractTypeDeclaration { // true if this type parameter represents t in type t;
      get { return parent == null; }
    }
    public bool IsToplevelScope { // true if this type parameter is on a toplevel (ie. class C<T>), and false if it is on a member (ie. method m<T>(...))
      get { return parent is TopLevelDecl; }
    }
    public int PositionalIndex; // which type parameter this is (ie. in C<S, T, U>, S is 0, T is 1 and U is 2).

    public TypeParameter(IToken tok, string name, EqualitySupportValue equalitySupport = EqualitySupportValue.Unspecified)
      : base(tok, name, null) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      EqualitySupport = equalitySupport;
    }
  }

  // Represents a submodule declaration at module level scope
  abstract public class ModuleDecl : TopLevelDecl
  {
    public ModuleSignature Signature; // filled in by resolution, in topological order.
    public int Height;
    public readonly bool Opened;
    public ModuleDecl(IToken tok, string name, ModuleDefinition parent, bool opened)
      : base(tok, name, parent, new List<TypeParameter>(), null) {
        Height = -1;
      Signature = null;
      Opened = opened;
    }
  }
  // Represents module X { ... }
  public class LiteralModuleDecl : ModuleDecl
  {
    public readonly ModuleDefinition ModuleDef;
    public LiteralModuleDecl(ModuleDefinition module, ModuleDefinition parent)
      : base(module.tok, module.Name, parent, false) {
      ModuleDef = module;
    }
  }
  // Represents "module name = path;", where name is a identifier and path is a possibly qualified name.
  public class AliasModuleDecl : ModuleDecl
  {
    public ModuleDecl ModuleReference; // should refer to another declaration somewhere. NOTE: cyclicity is possible, and should
                                       // be detected and warned.
    public readonly List<IToken> Path; // generated by the parser, this is looked up
    public ModuleDecl Root;            // the moduleDecl that Path[0] refers to.
    public AliasModuleDecl(List<IToken> path, IToken name, ModuleDefinition parent, bool opened)
      : base(name, name.val, parent, opened) {
       Contract.Requires(path != null && path.Count > 0);
       Path = path;
       ModuleReference = null;
    }
  }
  // Represents "module name as path [ = compilePath];", where name is a identifier and path is a possibly qualified name.
  public class AbstractModuleDecl : ModuleDecl
  {
    public ModuleDecl Root;
    public readonly List<IToken> Path;
    public ModuleDecl CompileRoot;
    public readonly List<IToken> CompilePath;
    public ModuleSignature OriginalSignature;
    
    public AbstractModuleDecl(List<IToken> path, IToken name, ModuleDefinition parent, List<IToken> compilePath, bool opened)
      : base(name, name.val, parent, opened) {
      Path = path;
      Root = null;
      CompilePath = compilePath;
    }
  }

  public class ModuleSignature {
    
    public readonly Dictionary<string, TopLevelDecl> TopLevels = new Dictionary<string, TopLevelDecl>();
    public readonly Dictionary<string, Tuple<DatatypeCtor, bool>> Ctors = new Dictionary<string, Tuple<DatatypeCtor, bool>>();
    public readonly Dictionary<string, MemberDecl> StaticMembers = new Dictionary<string, MemberDecl>();
    public ModuleDefinition ModuleDef = null; // Note: this is null if this signature does not correspond to a specific definition (i.e.
                                              // it is abstract). Otherwise, it points to that definition.
    public ModuleSignature CompileSignature = null; // This is the version of the signature that should be used at compile time.
    public ModuleSignature Refines = null;
    public bool IsGhost = false;
    public ModuleSignature() {}

    public bool FindSubmodule(string name, out ModuleSignature pp) {
      TopLevelDecl top;
      pp = null;
      if (TopLevels.TryGetValue(name, out top)) {
        if (top is ModuleDecl) {
          pp = ((ModuleDecl)top).Signature;
          return true;
        } else return false;
      } else return false;
    }
    
      
  }
  public class ModuleDefinition : Declaration {
    public readonly List<IToken> RefinementBaseName;  // null if no refinement base
    public ModuleDecl RefinementBaseRoot; // filled in early during resolution, corresponds to RefinementBaseName[0]
    public ModuleDefinition RefinementBase;  // filled in during resolution (null if no refinement base)
    
    public readonly List<TopLevelDecl/*!*/> TopLevelDecls = new List<TopLevelDecl/*!*/>();  // filled in by the parser; readonly after that
    public readonly Graph<MemberDecl/*!*/> CallGraph = new Graph<MemberDecl/*!*/>();  // filled in during resolution
    public int Height;  // height in the topological sorting of modules; filled in during resolution
    public readonly bool IsGhost;
    public readonly bool IsAbstract; // True iff this module represents an abstract interface
    private readonly bool IsBuiltinName; // true if this is something like _System that shouldn't have it's name mangled.
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(cce.NonNullElements(TopLevelDecls));
      Contract.Invariant(CallGraph != null);
    }

    public ModuleDefinition(IToken tok, string name, bool isGhost, bool isAbstract, List<IToken> refinementBase, Attributes attributes, bool isBuiltinName)
      : base(tok, name, attributes) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      RefinementBaseName = refinementBase;
      IsGhost = isGhost;
      IsAbstract = isAbstract;
      RefinementBaseRoot = null;
      RefinementBase = null;
      IsBuiltinName = isBuiltinName;
    }
    public virtual bool IsDefaultModule {
      get {
        return false;
      }
    }
    string compileName;
    new public string CompileName {
      get {
        if (compileName == null) {
          if (IsBuiltinName)
            compileName = Name;
          else
            compileName = "_" + Height.ToString() + "_" + NonglobalVariable.CompilerizeName(Name);
        }
        return compileName;
      }
    }

    public static IEnumerable<Function> AllFunctions(List<TopLevelDecl> declarations) {
      foreach (var d in declarations) {
        var cl = d as ClassDecl;
        if (cl != null) {
          foreach (var member in cl.Members) {
            var fn = member as Function;
            if (fn != null) {
              yield return fn;
            }
          }
        }
      }
    }
  }

  public class DefaultModuleDecl : ModuleDefinition {
    public DefaultModuleDecl() : base(Token.NoToken, "_module", false, false, null, null, true) {
    }
    public override bool IsDefaultModule {
      get {
        return true;
      }
    }
  }

  public abstract class TopLevelDecl : Declaration, TypeParameter.ParentType {
    public readonly ModuleDefinition Module;
    public readonly List<TypeParameter/*!*/>/*!*/ TypeArgs;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(cce.NonNullElements(TypeArgs));
    }

    public TopLevelDecl(IToken/*!*/ tok, string/*!*/ name, ModuleDefinition module, List<TypeParameter/*!*/>/*!*/ typeArgs, Attributes attributes)
      : base(tok, name, attributes) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(cce.NonNullElements(typeArgs));
      Module = module;
      TypeArgs = typeArgs;
    }

    public string FullName {
      get {
        return Module.Name + "." + Name;
      }
    }
    public string FullNameInContext(ModuleDefinition context) {
      if (Module == context) {
        return Name;
      } else {
        return Module.Name + "." + Name;
      }
    }
    public string FullCompileName {
      get {
        return Module.CompileName + "." + CompileName;
      }
    }
  }

  public class ClassDecl : TopLevelDecl {
    public readonly List<MemberDecl/*!*/>/*!*/ Members;
    public bool HasConstructor;  // filled in (early) during resolution; true iff there exists a member that is a Constructor
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(cce.NonNullElements(Members));
    }

    public ClassDecl(IToken/*!*/ tok, string/*!*/ name, ModuleDefinition/*!*/ module,
      List<TypeParameter/*!*/>/*!*/ typeArgs, [Captured] List<MemberDecl/*!*/>/*!*/ members, Attributes attributes)
      : base(tok, name, module, typeArgs, attributes) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(module != null);
      Contract.Requires(cce.NonNullElements(typeArgs));
      Contract.Requires(cce.NonNullElements(members));
      Members = members;
    }
    public virtual bool IsDefaultClass {
      get {
        return false;
      }
    }
  }

  public class DefaultClassDecl : ClassDecl {
    public DefaultClassDecl(ModuleDefinition/*!*/ module, [Captured] List<MemberDecl/*!*/>/*!*/ members)
      : base(Token.NoToken, "_default", module, new List<TypeParameter/*!*/>(), members, null) {
      Contract.Requires(module != null);
      Contract.Requires(cce.NonNullElements(members));
    }
    public override bool IsDefaultClass {
      get {
        return true;
      }
    }
  }

  public class ArrayClassDecl : ClassDecl {
    public readonly int Dims;
    public ArrayClassDecl(int dims, ModuleDefinition module)
    : base(Token.NoToken, BuiltIns.ArrayClassName(dims), module,
      new List<TypeParameter>(new TypeParameter[]{new TypeParameter(Token.NoToken, "arg")}),
      new List<MemberDecl>(), null)
    {
      Contract.Requires(1 <= dims);
      Contract.Requires(module != null);

      Dims = dims;
    }
  }

  public abstract class DatatypeDecl : TopLevelDecl {
    public readonly List<DatatypeCtor/*!*/>/*!*/ Ctors;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(cce.NonNullElements(Ctors));
      Contract.Invariant(1 <= Ctors.Count);
    }

    public DatatypeDecl(IToken/*!*/ tok, string/*!*/ name, ModuleDefinition/*!*/ module, List<TypeParameter/*!*/>/*!*/ typeArgs,
      [Captured] List<DatatypeCtor/*!*/>/*!*/ ctors, Attributes attributes)
      : base(tok, name, module, typeArgs, attributes) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(module != null);
      Contract.Requires(cce.NonNullElements(typeArgs));
      Contract.Requires(cce.NonNullElements(ctors));
      Contract.Requires(1 <= ctors.Count);
      Ctors = ctors;
    }
    public bool HasFinitePossibleValues {
      get {
        return (TypeArgs.Count == 0 && Ctors.TrueForAll(ctr => ctr.Formals.Count == 0));
      }
    }
  }

  public class IndDatatypeDecl : DatatypeDecl
  {
    public DatatypeCtor DefaultCtor;  // set during resolution
    public bool[] TypeParametersUsedInConstructionByDefaultCtor;  // set during resolution; has same length as the number of type arguments

    public enum ES { NotYetComputed, Never, ConsultTypeArguments }
    public ES EqualitySupport = ES.NotYetComputed;

    public IndDatatypeDecl(IToken/*!*/ tok, string/*!*/ name, ModuleDefinition/*!*/ module, List<TypeParameter/*!*/>/*!*/ typeArgs,
      [Captured] List<DatatypeCtor/*!*/>/*!*/ ctors, Attributes attributes)
      : base(tok, name, module, typeArgs, ctors, attributes) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(module != null);
      Contract.Requires(cce.NonNullElements(typeArgs));
      Contract.Requires(cce.NonNullElements(ctors));
      Contract.Requires(1 <= ctors.Count);
    }
  }

  public class CoDatatypeDecl : DatatypeDecl
  {
    public CoDatatypeDecl(IToken/*!*/ tok, string/*!*/ name, ModuleDefinition/*!*/ module, List<TypeParameter/*!*/>/*!*/ typeArgs,
      [Captured] List<DatatypeCtor/*!*/>/*!*/ ctors, Attributes attributes)
      : base(tok, name, module, typeArgs, ctors, attributes) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(module != null);
      Contract.Requires(cce.NonNullElements(typeArgs));
      Contract.Requires(cce.NonNullElements(ctors));
      Contract.Requires(1 <= ctors.Count);
    }
  }

  public class DatatypeCtor : Declaration, TypeParameter.ParentType
  {
    public readonly List<Formal/*!*/>/*!*/ Formals;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(cce.NonNullElements(Formals));
      Contract.Invariant(Destructors != null);
      Contract.Invariant(
        Destructors.Count == 0 || // this is until resolution
        Destructors.Count == Formals.Count);  // after resolution
    }

    // TODO: One could imagine having a precondition on datatype constructors
    public DatatypeDecl EnclosingDatatype;  // filled in during resolution
    public SpecialField QueryField;  // filled in during resolution
    public List<SpecialField> Destructors = new List<SpecialField>();  // contents filled in during resolution; includes both implicit (not mentionable in source) and explicit destructors

    public DatatypeCtor(IToken/*!*/ tok, string/*!*/ name, [Captured] List<Formal/*!*/>/*!*/ formals, Attributes attributes)
      : base(tok, name, attributes) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(cce.NonNullElements(formals));
      this.Formals = formals;
    }

    public string FullName {
      get {
        Contract.Requires(EnclosingDatatype != null);
        Contract.Ensures(Contract.Result<string>() != null);

        return "#" + EnclosingDatatype.FullCompileName + "." + Name;
      }
    }
  }

  public interface ICodeContext : ICallable
  {
    bool IsGhost { get; }
    bool IsStatic { get; }
    List<TypeParameter> TypeArgs { get; }
    List<Formal> Ins { get ; }
    List<Formal> Outs { get; }
    Specification<FrameExpression> Modifies { get; }
    Specification<Expression> Decreases { get; }
    ModuleDefinition EnclosingModule { get; }  // to be called only after signature-resolution is complete
    bool MustReverify { get; }
  }

  public class IteratorDecl : ClassDecl, ICodeContext
  {
    public readonly List<Formal> Ins;
    public readonly List<Formal> Outs;
    public readonly Specification<FrameExpression> Reads;
    public readonly Specification<FrameExpression> Modifies;
    public readonly Specification<Expression> Decreases;
    public bool InferredDecreases;  // fill in during resolution/registration
    public readonly List<MaybeFreeExpression> Requires;
    public readonly List<MaybeFreeExpression> Ensures;
    public readonly List<MaybeFreeExpression> YieldRequires;
    public readonly List<MaybeFreeExpression> YieldEnsures;
    public readonly BlockStmt Body;
    public readonly bool SignatureIsOmitted;
    public readonly List<Field> OutsFields;
    public readonly List<Field> OutsHistoryFields;  // these are the 'xs' variables
    public readonly List<Field> DecreasesFields;  // filled in during resolution
    public SpecialField Member_Modifies;  // filled in during resolution
    public SpecialField Member_Reads;  // filled in during resolution
    public SpecialField Member_New;  // filled in during resolution
    public Constructor Member_Init;  // created during registration phase of resolution; its specification is filled in during resolution
    public Predicate Member_Valid;  // created during registration phase of resolution; its specification is filled in during resolution
    public Method Member_MoveNext;  // created during registration phase of resolution; its specification is filled in during resolution
    public IteratorDecl(IToken tok, string name, ModuleDefinition module, List<TypeParameter> typeArgs,
                        List<Formal> ins, List<Formal> outs,
                        Specification<FrameExpression> reads, Specification<FrameExpression> mod, Specification<Expression> decreases,
                        List<MaybeFreeExpression> requires,
                        List<MaybeFreeExpression> ensures,
                        List<MaybeFreeExpression> yieldRequires,
                        List<MaybeFreeExpression> yieldEnsures,
                        BlockStmt body, Attributes attributes, bool signatureIsOmitted)
      : base(tok, name, module, typeArgs, new List<MemberDecl>(), attributes)
    {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(module != null);
      Contract.Requires(typeArgs != null);
      Contract.Requires(ins != null);
      Contract.Requires(outs != null);
      Contract.Requires(reads != null);
      Contract.Requires(mod != null);
      Contract.Requires(decreases != null);
      Contract.Requires(requires != null);
      Contract.Requires(ensures != null);
      Contract.Requires(yieldRequires != null);
      Contract.Requires(yieldEnsures != null);
      Ins = ins;
      Outs = outs;
      Reads = reads;
      Modifies = mod;
      Decreases = decreases;
      Requires = requires;
      Ensures = ensures;
      YieldRequires = yieldRequires;
      YieldEnsures = yieldEnsures;
      Body = body;
      SignatureIsOmitted = signatureIsOmitted;

      OutsFields = new List<Field>();
      OutsHistoryFields = new List<Field>();
      DecreasesFields = new List<Field>();
    }

    bool ICodeContext.IsGhost { get { return false; } }
    bool ICodeContext.IsStatic { get { return true; } }
    List<TypeParameter> ICodeContext.TypeArgs { get { return this.TypeArgs; } }
    List<Formal> ICodeContext.Ins { get { return this.Ins; } }
    List<Formal> ICodeContext.Outs { get { return this.Outs; } }
    Specification<FrameExpression> ICodeContext.Modifies { get { return this.Modifies; } }
    Specification<Expression> ICodeContext.Decreases { get { return this.Decreases; } }
    ModuleDefinition ICodeContext.EnclosingModule { get { return this.Module; } }
    bool ICodeContext.MustReverify { get { return false; } }
  }

  /// <summary>
  /// An "ICallable" is a function, method, or iterator.
  /// </summary>
  public interface ICallable { }

  public abstract class MemberDecl : Declaration {
    public readonly bool IsStatic;
    public readonly bool IsGhost;
    public TopLevelDecl EnclosingClass;  // filled in during resolution

    public MemberDecl(IToken tok, string name, bool isStatic, bool isGhost, Attributes attributes)
      : base(tok, name, attributes) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      IsStatic = isStatic;
      IsGhost = isGhost;
    }
    /// <summary>
    /// Returns className+"."+memberName.  Available only after resolution.
    /// </summary>
    public string FullName {
      get {
        Contract.Requires(EnclosingClass != null);
        Contract.Ensures(Contract.Result<string>() != null);

        return EnclosingClass.FullName + "." + Name;
      }
    }
    public string FullNameInContext(ModuleDefinition context) {
      Contract.Requires(EnclosingClass != null);
      Contract.Ensures(Contract.Result<string>() != null);

      return EnclosingClass.FullNameInContext(context) + "." + Name;
    }
    public override string CompileName {
      get {
        var nm = base.CompileName;
        if (this.Name == EnclosingClass.Name) {
          nm = "_" + nm;
        }
        return nm;
      }
    }
    public string FullCompileName {
      get {
        Contract.Requires(EnclosingClass != null);
        Contract.Ensures(Contract.Result<string>() != null);

        return EnclosingClass.FullCompileName + "." + CompileName;
      }
    }
  }

  public class Field : MemberDecl {
    public readonly bool IsMutable;  // says whether or not the field can ever change values
    public readonly bool IsUserMutable;  // says whether or not code is allowed to assign to the field (IsUserMutable implies IsMutable)
    public readonly Type Type;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Type != null);
      Contract.Invariant(!IsUserMutable || IsMutable);  // IsUserMutable ==> IsMutable
    }

    public Field(IToken tok, string name, bool isGhost, Type type, Attributes attributes)
      : this(tok, name, isGhost, true, true, type, attributes) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(type != null);
    }

    public Field(IToken tok, string name, bool isGhost, bool isMutable, bool isUserMutable, Type type, Attributes attributes)
      : base(tok, name, false, isGhost, attributes) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(type != null);
      Contract.Requires(!isUserMutable || isMutable);
      IsMutable = isMutable;
      IsUserMutable = isUserMutable;
      Type = type;
    }
  }

  public class SpecialField : Field
  {
    public readonly string CompiledName;
    public readonly string PreString;
    public readonly string PostString;
    public SpecialField(IToken tok, string name, string compiledName, string preString, string postString, bool isGhost, bool isMutable, bool isUserMutable, Type type, Attributes attributes)
      : base(tok, name, isGhost, isMutable, isUserMutable, type, attributes) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(compiledName != null);
      Contract.Requires(preString != null);
      Contract.Requires(postString != null);
      Contract.Requires(!isUserMutable || isMutable);
      Contract.Requires(type != null);

      CompiledName = compiledName;
      PreString = preString;
      PostString = postString;
    }
  }

  public class DatatypeDestructor : SpecialField
  {
    public readonly DatatypeCtor EnclosingCtor;
    public readonly Formal CorrespondingFormal;

    public DatatypeDestructor(IToken tok, DatatypeCtor enclosingCtor, Formal correspondingFormal, string name, string compiledName, string preString, string postString, bool isGhost, Type type, Attributes attributes)
      : base(tok, name, compiledName, preString, postString, isGhost, false, false, type, attributes)
    {
      Contract.Requires(tok != null);
      Contract.Requires(enclosingCtor != null);
      Contract.Requires(correspondingFormal != null);
      Contract.Requires(name != null);
      Contract.Requires(compiledName != null);
      Contract.Requires(preString != null);
      Contract.Requires(postString != null);
      Contract.Requires(type != null);
      EnclosingCtor = enclosingCtor;
      CorrespondingFormal = correspondingFormal;
    }
  }

  public class ArbitraryTypeDecl : TopLevelDecl, TypeParameter.ParentType
  {
    public readonly TypeParameter TheType;
    public TypeParameter.EqualitySupportValue EqualitySupport {
      get { return TheType.EqualitySupport; }
    }
    public bool MustSupportEquality {
      get { return TheType.MustSupportEquality; }
    }
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(TheType != null && Name == TheType.Name);
    }

    public ArbitraryTypeDecl(IToken/*!*/ tok, string/*!*/ name, ModuleDefinition/*!*/ module, TypeParameter.EqualitySupportValue equalitySupport, Attributes attributes)
      : base(tok, name, module, new List<TypeParameter>(), attributes) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(module != null);
      TheType = new TypeParameter(tok, name, equalitySupport);
    }
  }

  [ContractClass(typeof(IVariableContracts))]
  public interface IVariable {
    string/*!*/ Name {
      get;
    }
    string/*!*/ DisplayName {  // what the user thinks he wrote
      get;
    }
    string/*!*/ UniqueName {
      get;
    }
    string/*!*/ CompileName {
      get;
    }
    Type/*!*/ Type {
      get;
    }
    bool IsMutable {
      get;
    }
    bool IsGhost {
      get;
    }
  }
  [ContractClassFor(typeof(IVariable))]
  public abstract class IVariableContracts : IVariable {
    public string Name {
      get {
        Contract.Ensures(Contract.Result<string>() != null);
        throw new NotImplementedException();  // this getter implementation is here only so that the Ensures contract can be given here
      }
    }
    public string DisplayName {
      get {
        Contract.Ensures(Contract.Result<string>() != null);
        throw new NotImplementedException();  // this getter implementation is here only so that the Ensures contract can be given here
      }
    }
    public string UniqueName {
      get {
        Contract.Ensures(Contract.Result<string>() != null);
        throw new NotImplementedException();  // this getter implementation is here only so that the Ensures contract can be given here
      }
    }
    public string CompileName {
      get {
        Contract.Ensures(Contract.Result<string>() != null);
        throw new NotImplementedException();  // this getter implementation is here only so that the Ensures contract can be given here
      }
    }
    public Type Type {
      get {
        Contract.Ensures(Contract.Result<Type>() != null);
        throw new NotImplementedException();  // this getter implementation is here only so that the Ensures contract can be given here
      }
    }
    public bool IsMutable {
      get {
        throw new NotImplementedException();
      }
    }
    public bool IsGhost {
      get {
        throw new NotImplementedException();
      }
    }
  }

  public abstract class NonglobalVariable : IVariable {
    public readonly IToken tok;
    readonly string name;

    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(tok != null);
      Contract.Invariant(name != null);
      Contract.Invariant(type != null);
    }

    public string Name {
      get {
        Contract.Ensures(Contract.Result<string>() != null);
        return name;
      }
    }
    public string/*!*/ DisplayName {
      get { return VarDecl.DisplayNameHelper(this); }
    }
    readonly int varId = varIdCount++;
    public string UniqueName {
      get {
        Contract.Ensures(Contract.Result<string>() != null);
        return name + "#" + varId;
      }
    }
    static char[] specialChars = new char[] { '\'', '_', '?', '\\' };
    public static string CompilerizeName(string nm) {
      string name = null;
      int i = 0;
      while (true) {
        int j = nm.IndexOfAny(specialChars, i);
        if (j == -1) {
          if (i == 0) {
            return nm;  // this is the common case
          } else {
            return name + nm.Substring(i);
          }
        } else {
          string nxt = nm.Substring(i, j - i);
          name = name == null ? nxt : name + nxt;
          switch (nm[j]) {
            case '\'': name += "_k"; break;
            case '_': name += "__"; break;
            case '?': name += "_q"; break;
            case '\\': name += "_b"; break;
            default:
              Contract.Assume(false);  // unexpected character
              break;
          }
          i = j + 1;
          if (i == nm.Length) {
            return name;
          }
        }
      }
    }
    protected string compileName;
    public virtual string CompileName {
      get {
        if (compileName == null) {
          compileName = string.Format("_{0}_{1}", varId, CompilerizeName(name));
        }
        return compileName;
      }
    }
    Type type;
    //[Pure(false)]  // TODO: if Type gets the status of [Frozen], then this attribute is not needed
    public Type/*!*/ Type {
      get {
        Contract.Ensures(Contract.Result<Type>() != null);
        return type.Normalize();
      }
    }
    public abstract bool IsMutable {
      get;
    }
    bool isGhost;  // readonly, except for BoundVar's of match expressions/statements during resolution
    public bool IsGhost {
      get {
        return isGhost;
      }
      set {
        isGhost = value;
      }
    }

    public NonglobalVariable(IToken tok, string name, Type type, bool isGhost) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(type != null);
      this.tok = tok;
      this.name = name;
      this.type = type;
      this.isGhost = isGhost;
    }

    internal static int varIdCount;  // this varIdCount is used for both NonglobalVariable's and VarDecl's.
  }

  public class Formal : NonglobalVariable {
    public readonly bool InParam;  // true to in-parameter, false for out-parameter
    public override bool IsMutable {
      get {
        return !InParam;
      }
    }

    public Formal(IToken/*!*/ tok, string/*!*/ name, Type/*!*/ type, bool inParam, bool isGhost)
      : base(tok, name, type, isGhost) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(type != null);
      InParam = inParam;
    }

    public bool HasName {
      get {
        return !Name.StartsWith("#");
      }
    }
    public override string CompileName {
      get {
        if (compileName == null) {
          compileName = CompilerizeName(Name);
        }
        return compileName;
      }
    }
  }

  /// <summary>
  /// A "ThisSurrogate" is used during translation time to make the treatment of the receiver more similar to
  /// the treatment of other in-parameters.
  /// </summary>
  public class ThisSurrogate : Formal
  {
    public ThisSurrogate(IToken tok, Type type)
      : base(tok, "this", type, true, false) {
      Contract.Requires(tok != null);
      Contract.Requires(type != null);
    }
  }

  public class BoundVar : NonglobalVariable {
    public override bool IsMutable {
      get {
        return false;
      }
    }

    public BoundVar(IToken/*!*/ tok, string/*!*/ name, Type/*!*/ type)
      : base(tok, name, type, false) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(type != null);
    }
  }

  public class Function : MemberDecl, TypeParameter.ParentType, ICallable {
    public bool IsRecursive;  // filled in during resolution
    public readonly List<TypeParameter/*!*/>/*!*/ TypeArgs;
    public readonly IToken OpenParen;  // can be null (for predicates), if there are no formals
    public readonly List<Formal/*!*/>/*!*/ Formals;
    public readonly Type/*!*/ ResultType;
    public readonly List<Expression/*!*/>/*!*/ Req;
    public readonly List<FrameExpression/*!*/>/*!*/ Reads;
    public readonly List<Expression/*!*/>/*!*/ Ens;
    public readonly Specification<Expression>/*!*/ Decreases;
    public Expression Body;  // an extended expression; Body is readonly after construction, except for any kind of rewrite that may take place around the time of resolution
    public readonly bool SignatureIsOmitted;  // is "false" for all Function objects that survive into resolution
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(cce.NonNullElements(TypeArgs));
      Contract.Invariant(cce.NonNullElements(Formals));
      Contract.Invariant(ResultType != null);
      Contract.Invariant(cce.NonNullElements(Req));
      Contract.Invariant(cce.NonNullElements(Reads));
      Contract.Invariant(cce.NonNullElements(Ens));
      Contract.Invariant(Decreases != null);
    }

    /// <summary>
    /// Note, functions are "ghost" by default; a non-ghost function is called a "function method".
    /// </summary>
    public Function(IToken tok, string name, bool isStatic, bool isGhost,
                    List<TypeParameter> typeArgs, IToken openParen, List<Formal> formals, Type resultType,
                    List<Expression> req, List<FrameExpression> reads, List<Expression> ens, Specification<Expression> decreases,
                    Expression body, Attributes attributes, bool signatureOmitted)
      : base(tok, name, isStatic, isGhost, attributes) {

      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(cce.NonNullElements(typeArgs));
      Contract.Requires(cce.NonNullElements(formals));
      Contract.Requires(resultType != null);
      Contract.Requires(cce.NonNullElements(req));
      Contract.Requires(cce.NonNullElements(reads));
      Contract.Requires(cce.NonNullElements(ens));
      Contract.Requires(decreases != null);
      this.TypeArgs = typeArgs;
      this.OpenParen = openParen;
      this.Formals = formals;
      this.ResultType = resultType;
      this.Req = req;
      this.Reads = reads;
      this.Ens = ens;
      this.Decreases = decreases;
      this.Body = body;
      this.SignatureIsOmitted = signatureOmitted;
    }
  }

  public class Predicate : Function
  {
    public enum BodyOriginKind
    {
      OriginalOrInherited,  // this predicate definition is new (and the predicate may or may not have a body), or the predicate's body (whether or not it exists) is being inherited unmodified (from the previous refinement--it may be that the inherited body was itself an extension, for example)
      DelayedDefinition,  // this predicate declaration provides, for the first time, a body--the declaration refines a previously declared predicate, but the previous one had no body
      Extension  // this predicate extends the definition of a predicate with a body in a module being refined
    }
    public readonly BodyOriginKind BodyOrigin;
    public Predicate(IToken tok, string name, bool isStatic, bool isGhost,
                     List<TypeParameter> typeArgs, IToken openParen, List<Formal> formals,
                     List<Expression> req, List<FrameExpression> reads, List<Expression> ens, Specification<Expression> decreases,
                     Expression body, BodyOriginKind bodyOrigin, Attributes attributes, bool signatureOmitted)
      : base(tok, name, isStatic, isGhost, typeArgs, openParen, formals, new BoolType(), req, reads, ens, decreases, body, attributes, signatureOmitted) {
      Contract.Requires(bodyOrigin == Predicate.BodyOriginKind.OriginalOrInherited || body != null);
      BodyOrigin = bodyOrigin;
    }
  }

  public class CoPredicate : Function
  {
    public readonly List<FunctionCallExpr> Uses = new List<FunctionCallExpr>();  // filled in during resolution, used by verifier

    public CoPredicate(IToken tok, string name, bool isStatic,
                     List<TypeParameter> typeArgs, IToken openParen, List<Formal> formals,
                     List<Expression> req, List<FrameExpression> reads, List<Expression> ens,
                     Expression body, Attributes attributes, bool signatureOmitted)
      : base(tok, name, isStatic, true, typeArgs, openParen, formals, new BoolType(),
             req, reads, ens, new Specification<Expression>(new List<Expression>(), null), body, attributes, signatureOmitted) {
    }
  }

  public class Method : MemberDecl, TypeParameter.ParentType, ICodeContext
  {
    public readonly bool SignatureIsOmitted;
    public bool MustReverify;
    public readonly List<TypeParameter/*!*/>/*!*/ TypeArgs;
    public readonly List<Formal/*!*/>/*!*/ Ins;
    public readonly List<Formal/*!*/>/*!*/ Outs;
    public readonly List<MaybeFreeExpression/*!*/>/*!*/ Req;
    public readonly Specification<FrameExpression>/*!*/ Mod;
    public readonly List<MaybeFreeExpression/*!*/>/*!*/ Ens;
    public readonly Specification<Expression>/*!*/ Decreases;
    public BlockStmt Body;  // Body is readonly after construction, except for any kind of rewrite that may take place around the time of resolution
    public bool IsTailRecursive;  // filled in during resolution

    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(cce.NonNullElements(TypeArgs));
      Contract.Invariant(cce.NonNullElements(Ins));
      Contract.Invariant(cce.NonNullElements(Outs));
      Contract.Invariant(cce.NonNullElements(Req));
      Contract.Invariant(Mod != null);
      Contract.Invariant(cce.NonNullElements(Ens));
      Contract.Invariant(Decreases != null);
    }

    public Method(IToken tok, string name,
                  bool isStatic, bool isGhost,
                  [Captured] List<TypeParameter/*!*/>/*!*/ typeArgs,
                  [Captured] List<Formal/*!*/>/*!*/ ins, [Captured] List<Formal/*!*/>/*!*/ outs,
                  [Captured] List<MaybeFreeExpression/*!*/>/*!*/ req, [Captured] Specification<FrameExpression>/*!*/ mod,
                  [Captured] List<MaybeFreeExpression/*!*/>/*!*/ ens,
                  [Captured] Specification<Expression>/*!*/ decreases,
                  [Captured] BlockStmt body,
                  Attributes attributes, bool signatureOmitted)
      : base(tok, name, isStatic, isGhost, attributes) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(cce.NonNullElements(typeArgs));
      Contract.Requires(cce.NonNullElements(ins));
      Contract.Requires(cce.NonNullElements(outs));
      Contract.Requires(cce.NonNullElements(req));
      Contract.Requires(mod != null);
      Contract.Requires(cce.NonNullElements(ens));
      Contract.Requires(decreases != null);
      this.TypeArgs = typeArgs;
      this.Ins = ins;
      this.Outs = outs;
      this.Req = req;
      this.Mod = mod;
      this.Ens = ens;
      this.Decreases = decreases;
      this.Body = body;
      this.SignatureIsOmitted = signatureOmitted;
      MustReverify = false;
    }

    bool ICodeContext.IsGhost { get { return this.IsGhost; } }
    bool ICodeContext.IsStatic { get { return this.IsStatic; } }
    List<TypeParameter> ICodeContext.TypeArgs { get { return this.TypeArgs; } }
    List<Formal> ICodeContext.Ins { get { return this.Ins; } }
    List<Formal> ICodeContext.Outs { get { return this.Outs; } }
    Specification<FrameExpression> ICodeContext.Modifies { get { return Mod; } }
    Specification<Expression> ICodeContext.Decreases { get { return this.Decreases; } }
    ModuleDefinition ICodeContext.EnclosingModule {
      get {
        Contract.Assert(this.EnclosingClass != null);  // this getter is supposed to be called only after signature-resolution is complete
        return this.EnclosingClass.Module;
      }
    }
    bool ICodeContext.MustReverify { get { return this.MustReverify; } }
  }

  public class Constructor : Method
  {
    public Constructor(IToken tok, string name,
                  [Captured] List<TypeParameter/*!*/>/*!*/ typeArgs,
                  [Captured] List<Formal/*!*/>/*!*/ ins,
                  [Captured] List<MaybeFreeExpression/*!*/>/*!*/ req, [Captured] Specification<FrameExpression>/*!*/ mod,
                  [Captured] List<MaybeFreeExpression/*!*/>/*!*/ ens,
                  [Captured] Specification<Expression>/*!*/ decreases,
                  [Captured] BlockStmt body,
                  Attributes attributes, bool signatureOmitted)
      : base(tok, name, false, false, typeArgs, ins, new List<Formal>(), req, mod, ens, decreases, body, attributes, signatureOmitted) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(cce.NonNullElements(typeArgs));
      Contract.Requires(cce.NonNullElements(ins));
      Contract.Requires(cce.NonNullElements(req));
      Contract.Requires(mod != null);
      Contract.Requires(cce.NonNullElements(ens));
      Contract.Requires(decreases != null);
    }

    public bool HasName {
      get {
        return Name != "_ctor";
      }
    }
  }

  public class CoMethod : Method
  {
    public CoMethod(IToken tok, string name,
                  bool isStatic,
                  List<TypeParameter/*!*/>/*!*/ typeArgs,
                  List<Formal/*!*/>/*!*/ ins, [Captured] List<Formal/*!*/>/*!*/ outs,
                  List<MaybeFreeExpression/*!*/>/*!*/ req, [Captured] Specification<FrameExpression>/*!*/ mod,
                  List<MaybeFreeExpression/*!*/>/*!*/ ens,
                  Specification<Expression>/*!*/ decreases,
                  BlockStmt body,
                  Attributes attributes, bool signatureOmitted)
      : base(tok, name, isStatic, true, typeArgs, ins, outs, req, mod, ens, decreases, body, attributes, signatureOmitted) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(cce.NonNullElements(typeArgs));
      Contract.Requires(cce.NonNullElements(ins));
      Contract.Requires(cce.NonNullElements(outs));
      Contract.Requires(cce.NonNullElements(req));
      Contract.Requires(mod != null);
      Contract.Requires(cce.NonNullElements(ens));
      Contract.Requires(decreases != null);
    }
  }

  // ------------------------------------------------------------------------------------------------------

  public abstract class Statement {
    public readonly IToken Tok;
    public LList<Label> Labels;  // mutable during resolution

    private Attributes attributes;
    public Attributes Attributes {
      get {
        return attributes;
      }
      set {
        attributes = value;
      }
    }

    public bool HasAttributes() {
      return Attributes != null;
    }

    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Tok != null);
    }

    public bool IsGhost;  // filled in by resolution

    public Statement(IToken tok, Attributes attrs) {
      Contract.Requires(tok != null);
      this.Tok = tok;
      this.attributes = attrs;
    }

    public Statement(IToken tok)
      : this(tok, null) {
      Contract.Requires(tok != null);
      this.Tok = tok;
    }

    /// <summary>
    /// Returns the non-null substatements of the Statements.
    /// </summary>
    public virtual IEnumerable<Statement> SubStatements {
      get { yield break; }
    }

    /// <summary>
    /// Returns the non-null expressions of this statement proper (that is, do not include the expressions of substatements).
    /// </summary>
    public virtual IEnumerable<Expression> SubExpressions {
      get { yield break; }
    }
  }

  public class LList<T>
  {
    public readonly T Data;
    public readonly LList<T> Next;
    const LList<T> Empty = null;

    public LList(T d, LList<T> next) {
      Data = d;
      Next = next;
    }

    public static LList<T> Append(LList<T> a, LList<T> b) {
      if (a == null) return b;
      return new LList<T>(a.Data, Append(a.Next, b));
      // pretend this is ML
    }
    public static int Count(LList<T> n) {
      int count = 0;
      while (n != null) {
        count++;
        n = n.Next;
      }
      return count;
    }
  }

  public class Label
  {
    public readonly IToken Tok;
    public readonly string Name;
    public readonly int UniqueId;
    static int nodes = 0;

    public Label(IToken tok, string label) {
      Contract.Requires(tok != null);
      Tok = tok;
      Name = label;
      UniqueId = nodes++;
    }
  }

  public abstract class PredicateStmt : Statement
  {
    public readonly Expression Expr;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Expr != null);
    }

    public PredicateStmt(IToken tok, Expression expr, Attributes attrs)
      : base(tok, attrs) {
      Contract.Requires(tok != null);
      Contract.Requires(expr != null);
      this.Expr = expr;
    }

    public PredicateStmt(IToken tok, Expression expr)
      : this(tok, expr, null) {
      Contract.Requires(tok != null);
      Contract.Requires(expr != null);
      this.Expr = expr;
    }
    public override IEnumerable<Expression> SubExpressions {
      get {
        yield return Expr;
      }
    }
  }

  public class AssertStmt : PredicateStmt {
    public AssertStmt(IToken/*!*/ tok, Expression/*!*/ expr, Attributes attrs)
      : base(tok, expr, attrs) {
      Contract.Requires(tok != null);
      Contract.Requires(expr != null);
    }
  }

  public class AssumeStmt : PredicateStmt {
    public AssumeStmt(IToken/*!*/ tok, Expression/*!*/ expr, Attributes attrs)
      : base(tok, expr, attrs) {
      Contract.Requires(tok != null);
      Contract.Requires(expr != null);
    }
  }

  public class PrintStmt : Statement {
    public readonly List<Attributes.Argument/*!*/>/*!*/ Args;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(cce.NonNullElements(Args));
    }

    public PrintStmt(IToken tok, List<Attributes.Argument/*!*/>/*!*/ args)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(cce.NonNullElements(args));

      Args = args;
    }
    public override IEnumerable<Expression> SubExpressions {
      get {
        foreach (var arg in Args) {
          if (arg.E != null) {
            yield return arg.E;
          }
        }
      }
    }
  }

  public class BreakStmt : Statement {
    public readonly string TargetLabel;
    public readonly int BreakCount;
    public Statement TargetStmt;  // filled in during resolution
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(TargetLabel != null || 1 <= BreakCount);
    }

    public BreakStmt(IToken tok, string targetLabel)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(targetLabel != null);
      this.TargetLabel = targetLabel;
    }
    public BreakStmt(IToken tok, int breakCount)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(1 <= breakCount);
      this.BreakCount = breakCount;
    }
  }

  public abstract class ProduceStmt : Statement
  {
    public List<AssignmentRhs> rhss;
    public UpdateStmt hiddenUpdate;
    public ProduceStmt(IToken tok, List<AssignmentRhs> rhss)
      : base(tok) {
      Contract.Requires(tok != null);
      this.rhss = rhss;
      hiddenUpdate = null;
    }
    public override IEnumerable<Expression> SubExpressions {
      get {
        if (rhss != null) {
          foreach (var rhs in rhss) {
            foreach (var ee in rhs.SubExpressions) {
              yield return ee;
            }
          }
        }
      }
    }
  }

  public class ReturnStmt : ProduceStmt
  {
    public ReturnStmt(IToken tok, List<AssignmentRhs> rhss)
      : base(tok, rhss) {
      Contract.Requires(tok != null);
    }
  }

  public class YieldStmt : ProduceStmt
  {
    public YieldStmt(IToken tok, List<AssignmentRhs> rhss)
      : base(tok, rhss) {
      Contract.Requires(tok != null);
    }
  }

  public abstract class AssignmentRhs
  {
    public readonly IToken Tok;

    private Attributes attributes;
    public Attributes Attributes
    {
      get
      {
        return attributes;
      }
      set
      {
        attributes = value;
      }
    }

    public bool HasAttributes()
    {
      return Attributes != null;
    }

    internal AssignmentRhs(IToken tok, Attributes attrs = null) {
      Tok = tok;
      Attributes = attrs;
    }
    public abstract bool CanAffectPreviouslyKnownExpressions { get; }
    /// <summary>
    /// Returns the non-null subexpressions of the AssignmentRhs.
    /// </summary>
    public virtual IEnumerable<Expression> SubExpressions {
      get { yield break; }
    }
    /// <summary>
    /// Returns the non-null sub-statements of the AssignmentRhs.
    /// </summary>
    public virtual IEnumerable<Statement> SubStatements{
      get { yield break; }
    }
  }

  public class ExprRhs : AssignmentRhs
  {
    public readonly Expression Expr;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Expr != null);
    }

    public ExprRhs(Expression expr, Attributes attrs = null)
      : base(expr.tok, attrs)
    {
      Contract.Requires(expr != null);
      Expr = expr;
    }
    public override bool CanAffectPreviouslyKnownExpressions { get { return false; } }
    public override IEnumerable<Expression> SubExpressions {
      get {
        yield return Expr;
      }
    }
  }

  /// <summary>
  /// A TypeRhs represents one of three things, each having to do with allocating something in the heap:
  ///  * new T[EE]
  ///    This allocates an array of objects of type T (where EE is a list of expression)
  ///  * new C
  ///    This allocates an object of type C
  ///  * new C.Init(EE)
  ///    This allocates an object of type C and then invokes the method/constructor Init on it
  /// There are four ways to construct a TypeRhs syntactically:
  ///  * TypeRhs(T, EE)
  ///      -- represents new T[EE]
  ///  * TypeRhs(C)
  ///      -- represents new C
  ///  * TypeRhs(Path, null, EE)
  ///    Here, Path may either be of the form C.Init
  ///      -- represents new C.Init(EE)
  ///    or all of Path denotes a type
  ///      -- represents new C._ctor(EE), where _ctor is the default constructor for class C
  ///  * TypeRhs(Path, s, EE)
  ///    Here, Path must denote a type and s is a string that denotes the method/constructor Init
  ///      -- represents new Path.s(EE)
  /// </summary>
  public class TypeRhs : AssignmentRhs
  {
    /// <summary>
    /// If ArrayDimensions != null, then the TypeRhs represents "new EType[ArrayDimensions]"
    ///     and Arguments, OptionalNameComponent, and InitCall are all null.
    /// If Arguments == null, then the TypeRhs represents "new C"
    ///     and ArrayDimensions, OptionalNameComponent, and InitCall are all null.
    /// If OptionalNameComponent != null, then the TypeRhs represents "new EType.OptionalNameComponents(Arguments)"
    ///     and InitCall is filled in by resolution, and ArrayDimensions == null and Arguments != null.
    /// If OptionalNameComponent == null and Arguments != null, then the TypeRHS has not been resolved yet;
    ///   resolution will either produce an error or will chop off the last part of "EType" and move it to
    ///   OptionalNameComponent, after which the case above applies.
    /// </summary>
    public Type EType;  // almost readonly, except that resolution can split a given EType into a new EType plus a non-null OptionalNameComponent
    public readonly List<Expression> ArrayDimensions;
    public readonly List<Expression> Arguments;
    public Expression ReceiverArgumentForInitCall;  // may be filled during resolution (and, if so, had better be done before InitCall is filled)
    public string OptionalNameComponent;
    public CallStmt InitCall;  // may be null (and is definitely null for arrays), may be filled in during resolution
    public Type Type;  // filled in during resolution
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(EType != null);
      Contract.Invariant(ArrayDimensions == null || (Arguments == null && OptionalNameComponent == null && InitCall == null));
      Contract.Invariant(ArrayDimensions == null || 1 <= ArrayDimensions.Count);
      Contract.Invariant(OptionalNameComponent == null || (Arguments != null && ArrayDimensions == null));
    }

    public TypeRhs(IToken tok, Type type, List<Expression> arrayDimensions)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(type != null);
      Contract.Requires(arrayDimensions != null && 1 <= arrayDimensions.Count);
      EType = type;
      ArrayDimensions = arrayDimensions;
    }
    public TypeRhs(IToken tok, Type type)
      : base(tok)
    {
      Contract.Requires(tok != null);
      Contract.Requires(type != null);
      EType = type;
    }
    public TypeRhs(IToken tok, Type type, string optionalNameComponent, Expression receiverForInitCall, List<Expression> arguments)
      : base(tok)
    {
      Contract.Requires(tok != null);
      Contract.Requires(type != null);
      Contract.Requires(arguments != null);
      EType = type;
      OptionalNameComponent = optionalNameComponent;  // may be null
      ReceiverArgumentForInitCall = receiverForInitCall;
      Arguments = arguments;
    }
    public override bool CanAffectPreviouslyKnownExpressions {
      get {
        if (InitCall != null) {
          foreach (var mod in InitCall.Method.Mod.Expressions) {
            if (!(mod.E is ThisExpr)) {
              return true;
            }
          }
        }
        return false;
      }
    }

    public override IEnumerable<Expression> SubExpressions {
      get {
        if (ArrayDimensions != null) {
          foreach (var e in ArrayDimensions) {
            yield return e;
          }
        }
      }
    }
    public override IEnumerable<Statement> SubStatements {
      get {
        if (InitCall != null) {
          yield return InitCall;
        }
      }
    }
  }

  public class CallRhs : AssignmentRhs
  {
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Receiver != null);
      Contract.Invariant(MethodName != null);
      Contract.Invariant(cce.NonNullElements(Args));
    }

    public readonly Expression/*!*/ Receiver;
    public readonly string/*!*/ MethodName;
    public readonly List<Expression/*!*/>/*!*/ Args;
    public Method Method;  // filled in by resolution

    public CallRhs(IToken tok, Expression/*!*/ receiver, string/*!*/ methodName, List<Expression/*!*/>/*!*/ args)
      : base(tok)
    {
      Contract.Requires(tok != null);
      Contract.Requires(receiver != null);
      Contract.Requires(methodName != null);
      Contract.Requires(cce.NonNullElements(args));

      this.Receiver = receiver;
      this.MethodName = methodName;
      this.Args = args;
    }
    // TODO: Investigate this. For an initialization, this is true. But for existing objects, this is not true.
    public override bool CanAffectPreviouslyKnownExpressions {
      get {
        foreach (var mod in Method.Mod.Expressions) {
          if (!(mod.E is ThisExpr)) {
            return true;
          }
        }
        return false;
      }
    }
    public override IEnumerable<Expression> SubExpressions {
      get {
        yield return Receiver;
        foreach (var e in Args) {
          yield return e;
        }
      }
    }
  }

  public class HavocRhs : AssignmentRhs {
    public HavocRhs(IToken tok)
      : base(tok)
    {
    }
    public override bool CanAffectPreviouslyKnownExpressions { get { return false; } }
  }

  public abstract class ConcreteSyntaxStatement : Statement
  {
    public List<Statement> ResolvedStatements = new List<Statement>();  // contents filled in during resolution
    public ConcreteSyntaxStatement(IToken tok)
      : base(tok) {
    }

    public override IEnumerable<Statement> SubStatements {
      get { return ResolvedStatements; }
    }
  }

  public class VarDeclStmt : ConcreteSyntaxStatement
  {
    public readonly List<VarDecl> Lhss;
    public readonly ConcreteUpdateStatement Update;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(cce.NonNullElements(Lhss));
    }

    public VarDeclStmt(IToken tok, List<VarDecl> lhss, ConcreteUpdateStatement update)
      : base(tok)
    {
      Contract.Requires(lhss != null);

      Lhss = lhss;
      Update = update;
    }
  }

  /// <summary>
  /// Common superclass of UpdateStmt and AssignSuchThatStmt.
  /// </summary>
  public abstract class ConcreteUpdateStatement : ConcreteSyntaxStatement
  {
    public readonly List<Expression> Lhss;
    public ConcreteUpdateStatement(IToken tok, List<Expression> lhss)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(cce.NonNullElements(lhss));
      Lhss = lhss;
    }
  }

  public class AssignSuchThatStmt : ConcreteUpdateStatement
  {
    public readonly Expression Expr;
    public readonly IToken AssumeToken;
    /// <summary>
    /// "assumeToken" is allowed to be "null", in which case the verifier will check that a RHS value exists.
    /// If "assumeToken" is non-null, then it should denote the "assume" keyword used in the statement.
    /// </summary>
    public AssignSuchThatStmt(IToken tok, List<Expression> lhss, Expression expr, IToken assumeToken)
      : base(tok, lhss) {
      Contract.Requires(tok != null);
      Contract.Requires(cce.NonNullElements(lhss));
      Contract.Requires(lhss.Count != 0);
      Contract.Requires(expr != null);
      Expr = expr;
      if (assumeToken != null) {
        AssumeToken = assumeToken;
      }
    }
    public override IEnumerable<Expression> SubExpressions {
      get {
        yield return Expr;
        foreach (var lhs in Lhss) {
          yield return lhs;
        }
      }
    }
  }

  public class UpdateStmt : ConcreteUpdateStatement
  {
    public readonly List<AssignmentRhs> Rhss;
    public readonly bool CanMutateKnownState;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(cce.NonNullElements(Lhss));
      Contract.Invariant(cce.NonNullElements(Rhss));
    }
    public UpdateStmt(IToken tok, List<Expression> lhss, List<AssignmentRhs> rhss)
      : base(tok, lhss)
    {
      Contract.Requires(tok != null);
      Contract.Requires(cce.NonNullElements(lhss));
      Contract.Requires(cce.NonNullElements(rhss));
      Contract.Requires(lhss.Count != 0 || rhss.Count == 1);
      Rhss = rhss;
      CanMutateKnownState = false;
    }
    public UpdateStmt(IToken tok, List<Expression> lhss, List<AssignmentRhs> rhss, bool mutate)
      : base(tok, lhss)
    {
      Contract.Requires(tok != null);
      Contract.Requires(cce.NonNullElements(lhss));
      Contract.Requires(cce.NonNullElements(rhss));
      Contract.Requires(lhss.Count != 0 || rhss.Count == 1);
      Rhss = rhss;
      CanMutateKnownState = mutate;
    }
  }

  public class AssignStmt : Statement {
    public readonly Expression/*!*/ Lhs;
    public readonly AssignmentRhs/*!*/ Rhs;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Lhs != null);
      Contract.Invariant(Rhs != null);
    }

    public AssignStmt(IToken tok, Expression lhs, AssignmentRhs rhs)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(lhs != null);
      Contract.Requires(rhs != null);
      this.Lhs = lhs;
      this.Rhs = rhs;
    }

    public override IEnumerable<Statement> SubStatements {
      get {
        var trhs = Rhs as TypeRhs;
        if (trhs != null && trhs.InitCall != null) {
          yield return trhs.InitCall;
        }
      }
    }

    public override IEnumerable<Expression> SubExpressions {
      get {
        yield return Lhs;
        foreach (var ee in Rhs.SubExpressions) {
          yield return ee;
        }
      }
    }

    /// <summary>
    /// This method assumes "lhs" has been successfully resolved.
    /// </summary>
    public static bool LhsIsToGhost(Expression lhs) {
      Contract.Requires(lhs != null);
      lhs = lhs.Resolved;
      if (lhs is IdentifierExpr) {
        var x = (IdentifierExpr)lhs;
        return x.Var.IsGhost;
      } else if (lhs is FieldSelectExpr) {
        var x = (FieldSelectExpr)lhs;
        return x.Field.IsGhost;
      } else {
        // LHS denotes an array element, which is always non-ghost
        return false;
      }
    }
  }

  public class VarDecl : Statement, IVariable {
    readonly string/*!*/ name;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(name != null);
      Contract.Invariant(OptionalType != null);
    }

    public VarDecl(IToken tok, string name, Type type, bool isGhost)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Contract.Requires(type != null);  // can be a proxy, though

      this.name = name;
      this.OptionalType = type;
      this.IsGhost = isGhost;
    }

    public string/*!*/ Name {
      get {
        Contract.Ensures(Contract.Result<string>() != null);
        return name;
      }
    }
    public static bool HasWildcardName(IVariable v) {
      Contract.Requires(v != null);
      return v.Name.StartsWith("_");
    }
    public static string DisplayNameHelper(IVariable v) {
      Contract.Requires(v != null);
      return HasWildcardName(v) ? "_" : v.Name;
    }
    public string/*!*/ DisplayName {
      get { return DisplayNameHelper(this); }
    }
    readonly int varId = NonglobalVariable.varIdCount++;
    public string/*!*/ UniqueName {
      get {
        Contract.Ensures(Contract.Result<string>() != null);
        return name + "#" + varId;
      }
    }
    string compileName;
    public string CompileName {
      get {
        if (compileName == null) {
          compileName = string.Format("_{0}_{1}", varId, NonglobalVariable.CompilerizeName(name));
        }
        return compileName;
      }
    }
    public readonly Type OptionalType;  // this is the type mentioned in the declaration, if any
    internal Type type;  // this is the declared or inferred type of the variable; it is non-null after resolution (even if resolution fails)
    //[Pure(false)]
    public Type Type {
      get {
        Contract.Ensures(Contract.Result<Type>() != null);

        Contract.Assume(type != null);  /* we assume object has been resolved */
        return type.Normalize();
      }
    }
    public bool IsMutable {
      get {
        return true;
      }
    }
    bool IVariable.IsGhost {
      get {
        return base.IsGhost;
      }
    }
    /// <summary>
    /// This method retrospectively makes the VarDecl a ghost.  It is to be used only during resolution.
    /// </summary>
    public void MakeGhost() {
      base.IsGhost = true;
    }
  }

  public class CallStmt : Statement {
    [ContractInvariantMethod]
    void ObjectInvariant() {
      //Contract.Invariant(Receiver != null);
      Contract.Invariant(MethodName != null);
      Contract.Invariant(cce.NonNullElements(Lhs));
      Contract.Invariant(cce.NonNullElements(Args));
    }

    public readonly List<Expression/*!*/>/*!*/ Lhs;
    public Expression Receiver;  // non-null after resolution
    public readonly string/*!*/ MethodName;
    public readonly List<Expression/*!*/>/*!*/ Args;
    public Dictionary<TypeParameter, Type> TypeArgumentSubstitutions;  // create, initialized, and used by resolution (could be deleted once all of resolution is done)
    public Method Method;  // filled in by resolution

    public CallStmt(IToken tok, List<Expression> lhs, Expression receiver, string methodName, List<Expression> args)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(cce.NonNullElements(lhs));
      Contract.Requires(receiver != null);
      Contract.Requires(methodName != null);
      Contract.Requires(cce.NonNullElements(args));

      this.Lhs = lhs;
      this.Receiver = receiver;
      this.MethodName = methodName;
      this.Args = args;
    }

    public override IEnumerable<Expression> SubExpressions {
      get {
        foreach (var ee in Lhs) {
          yield return ee;
        }
        yield return Receiver;
        foreach (var ee in Args) {
          yield return ee;
        }
      }
    }
  }

  public class BlockStmt : Statement {
    public readonly List<Statement/*!*/>/*!*/ Body;
    public BlockStmt(IToken/*!*/ tok, [Captured] List<Statement/*!*/>/*!*/ body)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(cce.NonNullElements(body));
      this.Body = body;
    }

    public override IEnumerable<Statement> SubStatements {
      get { return Body; }
    }
  }

  public class IfStmt : Statement {
    public readonly Expression Guard;
    public readonly BlockStmt Thn;
    public readonly Statement Els;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Thn != null);
      Contract.Invariant(Els == null || Els is BlockStmt || Els is IfStmt || Els is SkeletonStatement);
    }
    public IfStmt(IToken tok, Expression guard, BlockStmt thn, Statement els)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(thn != null);
      Contract.Requires(els == null || els is BlockStmt || els is IfStmt || els is SkeletonStatement);
      this.Guard = guard;
      this.Thn = thn;
      this.Els = els;
    }
    public override IEnumerable<Statement> SubStatements {
      get {
        yield return Thn;
        if (Els != null) {
          yield return Els;
        }
      }
    }
    public override IEnumerable<Expression> SubExpressions {
      get {
        if (Guard != null) {
          yield return Guard;
        }
      }
    }
  }

  public class GuardedAlternative
  {
    public readonly IToken Tok;
    public readonly Expression Guard;
    public readonly List<Statement> Body;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Tok != null);
      Contract.Invariant(Guard != null);
      Contract.Invariant(Body != null);
    }
    public GuardedAlternative(IToken tok, Expression guard, List<Statement> body)
    {
      Contract.Requires(tok != null);
      Contract.Requires(guard != null);
      Contract.Requires(body != null);
      this.Tok = tok;
      this.Guard = guard;
      this.Body = body;
    }
  }

  public class AlternativeStmt : Statement
  {
    public readonly List<GuardedAlternative> Alternatives;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Alternatives != null);
    }
    public AlternativeStmt(IToken tok, List<GuardedAlternative> alternatives)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(alternatives != null);
      this.Alternatives = alternatives;
    }
    public override IEnumerable<Statement> SubStatements {
      get {
        foreach (var alt in Alternatives) {
          foreach (var s in alt.Body) {
            yield return s;
          }
        }
      }
    }
    public override IEnumerable<Expression> SubExpressions {
      get {
        foreach (var alt in Alternatives) {
          yield return alt.Guard;
        }
      }
    }
  }

  public abstract class LoopStmt : Statement
  {
    public readonly List<MaybeFreeExpression/*!*/>/*!*/ Invariants;
    public readonly Specification<Expression>/*!*/ Decreases;
    public readonly Specification<FrameExpression>/*!*/ Mod;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(cce.NonNullElements(Invariants));
      Contract.Invariant(Decreases != null);
      Contract.Invariant(Mod != null);
    }
    public LoopStmt(IToken tok, List<MaybeFreeExpression/*!*/>/*!*/ invariants, Specification<Expression>/*!*/ decreases, Specification<FrameExpression>/*!*/ mod)
    : base(tok)
    {
      Contract.Requires(tok != null);
      Contract.Requires(cce.NonNullElements(invariants));
      Contract.Requires(decreases != null);
      Contract.Requires(mod != null);

      this.Invariants = invariants;
      this.Decreases = decreases;
      this.Mod = mod;
    }
    public override IEnumerable<Expression> SubExpressions {
      get {
        foreach (var mfe in Invariants) {
          yield return mfe.E;
        }
        if (Decreases.Expressions != null) {
          foreach (var e in Decreases.Expressions) {
            yield return e;
          }
        }
        if (Mod.Expressions != null) {
          foreach (var fe in Mod.Expressions) {
            yield return fe.E;
          }
        }
      }
    }
  }

  public class WhileStmt : LoopStmt
  {
    public readonly Expression Guard;
    public readonly BlockStmt/*!*/ Body;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Body != null);
    }

    public WhileStmt(IToken tok, Expression guard,
                     List<MaybeFreeExpression/*!*/>/*!*/ invariants, Specification<Expression>/*!*/ decreases, Specification<FrameExpression>/*!*/ mod,
                     BlockStmt/*!*/ body)
      : base(tok, invariants, decreases, mod) {
      Contract.Requires(tok != null);
      Contract.Requires(body != null);
      this.Guard = guard;
      this.Body = body;
    }

    public override IEnumerable<Statement> SubStatements {
      get {
        yield return Body;
      }
    }
    public override IEnumerable<Expression> SubExpressions {
      get {
        if (Guard != null) {
          yield return Guard;
        }
        foreach (var e in base.SubExpressions) {
          yield return e;
        }
      }
    }
  }

  /// <summary>
  /// This class is really just a WhileStmt, except that it serves the purpose of remembering if the object was created as the result of a refinement
  /// merge.
  /// </summary>
  public class RefinedWhileStmt : WhileStmt
  {
    public RefinedWhileStmt(IToken tok, Expression guard,
                            List<MaybeFreeExpression/*!*/>/*!*/ invariants, Specification<Expression>/*!*/ decreases, Specification<FrameExpression>/*!*/ mod,
                            BlockStmt/*!*/ body)
      : base(tok, guard, invariants, decreases, mod, body) {
      Contract.Requires(tok != null);
      Contract.Requires(body != null);
    }
  }

  public class AlternativeLoopStmt : LoopStmt
  {
    public readonly List<GuardedAlternative> Alternatives;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Alternatives != null);
    }
    public AlternativeLoopStmt(IToken tok,
                               List<MaybeFreeExpression/*!*/>/*!*/ invariants, Specification<Expression>/*!*/ decreases, Specification<FrameExpression>/*!*/ mod,
                               List<GuardedAlternative> alternatives)
      : base(tok, invariants, decreases, mod) {
      Contract.Requires(tok != null);
      Contract.Requires(alternatives != null);
      this.Alternatives = alternatives;
    }
    public override IEnumerable<Statement> SubStatements {
      get {
        foreach (var alt in Alternatives) {
          foreach (var s in alt.Body) {
            yield return s;
          }
        }
      }
    }
    public override IEnumerable<Expression> SubExpressions {
      get {
        foreach (var alt in Alternatives) {
          yield return alt.Guard;
        }
        foreach (var e in base.SubExpressions) {
          yield return e;
        }
      }
    }
  }

  public class ParallelStmt : Statement
  {
    public readonly List<BoundVar/*!*/> BoundVars;  // note, can be the empty list, in which case Range denotes "true"
    public readonly Expression/*!*/ Range;
    public readonly List<MaybeFreeExpression/*!*/>/*!*/ Ens;
    public readonly Statement Body;  // used only until resolution; afterwards, use BodyAssign

    public List<ComprehensionExpr.BoundedPool> Bounds;  // initialized and filled in by resolver
    // invariant: if successfully resolved, Bounds.Count == BoundVars.Count;

    /// <summary>
    /// Assign means there are no ensures clauses and the body consists of one update statement,
    ///   either to an object field or to an array.
    /// Call means there are no ensures clauses and the body consists of a single call to a (presumably
    ///   ghost, but non-ghost is also allowed) method with no out-parameters and an empty modifies
    ///   clause.
    /// Proof means there is at least one ensures clause, and the body consists of any (presumably ghost,
    ///   but non-ghost is also allowed) code without side effects on variables (including fields and array
    ///   elements) declared outside the body itself.
    /// Notes:
    /// * More kinds may be allowed in the future.
    /// * One could also allow Call to call non-ghost methods without side effects.  However, that
    ///   would seem pointless in the program, so they are disallowed (to avoid any confusion that
    ///   such use of the parallel statement might actually have a point).
    /// * One could allow Proof even without ensures clauses that "export" what was learned.
    ///   However, that might give the false impression that the body is nevertheless exported.
    /// </summary>
    public enum ParBodyKind { Assign, Call, Proof }
    public ParBodyKind Kind;  // filled in during resolution

    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(BoundVars != null);
      Contract.Invariant(Range != null);
      Contract.Invariant(BoundVars.Count != 0 || LiteralExpr.IsTrue(Range));
      Contract.Invariant(Ens != null);
      Contract.Invariant(Body != null);
    }

    public ParallelStmt(IToken tok, List<BoundVar> boundVars, Attributes attrs, Expression range, List<MaybeFreeExpression/*!*/>/*!*/ ens, Statement body)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(cce.NonNullElements(boundVars));
      Contract.Requires(range != null);
      Contract.Requires(boundVars.Count != 0 || LiteralExpr.IsTrue(range));
      Contract.Requires(cce.NonNullElements(ens));
      Contract.Requires(body != null);
      this.BoundVars = boundVars;
      this.Attributes = attrs;
      this.Range = range;
      this.Ens = ens;
      this.Body = body;
    }

    public Statement S0 {
      get {
        // dig into Body to find a single statement
        Statement s = this.Body;
        while (true) {
          var block = s as BlockStmt;
          if (block != null && block.Body.Count == 1) {
            s = block.Body[0];
          } else {
            var conc = s as ConcreteSyntaxStatement;
            if (conc != null && conc.ResolvedStatements.Count == 1) {
              s = conc.ResolvedStatements[0];
            } else {
              return s;
            }
          }
        }
      }
    }

    public override IEnumerable<Statement> SubStatements {
      get {
        yield return Body;
      }
    }
    public override IEnumerable<Expression> SubExpressions {
      get {
        yield return Range;
        foreach (var ee in Ens) {
          yield return ee.E;
        }
      }
    }
  }

  public class CalcStmt : Statement
  {
    public readonly BinaryExpr.Opcode/*!*/ Op; // main operator of the calculation
    public readonly List<Expression/*!*/> Lines;
    public readonly List<BlockStmt/*!*/> Hints;  // Hints[i] comes after line i; block statement is used as a container for multiple sub-hints
    public readonly List<BinaryExpr.Opcode?> CustomOps; // CustomOps[i] comes after line i; null denotes the absence of a custom operator
    public readonly List<BinaryExpr/*!*/> Steps; // expressions li op l<i + 1>, filled in during resolution in order to get the correct op
    public BinaryExpr Result; // expression l0 op ln, filled in during resolution in order to get the correct op

    public static readonly BinaryExpr.Opcode/*!*/ DefaultOp = BinaryExpr.Opcode.Eq; 

    [ContractInvariantMethod]
    void ObjectInvariant()
    {
      Contract.Invariant(ValidOp(Op));
      Contract.Invariant(Lines != null);
      Contract.Invariant(Hints != null);
      Contract.Invariant(CustomOps != null);
      Contract.Invariant(Steps != null);
      Contract.Invariant(Hints.Count == Math.Max(Lines.Count - 1, 0));
      Contract.Invariant(CustomOps.Count == Hints.Count);
    }

    public CalcStmt(IToken tok, BinaryExpr.Opcode/*!*/ op, List<Expression/*!*/> lines, List<BlockStmt/*!*/> hints, List<BinaryExpr.Opcode?> customOps)
      : base(tok)
    {
      Contract.Requires(tok != null);
      Contract.Requires(ValidOp(op));
      Contract.Requires(lines != null);
      Contract.Requires(hints != null);
      Contract.Requires(customOps != null);
      Contract.Requires(cce.NonNullElements(lines));
      Contract.Requires(cce.NonNullElements(hints));
      Contract.Requires(hints.Count == Math.Max(lines.Count - 1, 0));
      Contract.Requires(customOps.Count == hints.Count);
      this.Op = op;
      this.Lines = lines;
      this.Hints = hints;
      this.CustomOps = customOps;
      this.Steps = new List<BinaryExpr>();  
      this.Result = null;
    }

    public override IEnumerable<Statement> SubStatements
    {
      get {
        foreach (var h in Hints) {
          yield return h;
        }
      }
    }
    public override IEnumerable<Expression> SubExpressions
    {
      get {
        foreach (var l in Lines) {
            yield return l;
        }
      }
    }

    /// <summary>
    /// Is op a valid calculation operator (i.e. a transitive relational operator)?
    /// </summary>
    [Pure]
    public static bool ValidOp(BinaryExpr.Opcode op) {
      return op == BinaryExpr.Opcode.Eq || op == BinaryExpr.Opcode.Lt || op == BinaryExpr.Opcode.Le || op == BinaryExpr.Opcode.Gt || op == BinaryExpr.Opcode.Ge
        || op == BinaryExpr.Opcode.Neq
        || op == BinaryExpr.Opcode.Iff || op == BinaryExpr.Opcode.Imp;
    }

    /// <summary>
    /// Does op1 subsume op2 (i.e. forall x, y, z :: (x op1 y op2 z) || (x op2 y op1 z) ==> x op1 z)?
    /// </summary>
    [Pure]    
    private static bool Subsumes(BinaryExpr.Opcode op1, BinaryExpr.Opcode op2) {
      Contract.Requires(ValidOp(op1) && ValidOp(op2));
      if (op1 == BinaryExpr.Opcode.Neq || op2 == BinaryExpr.Opcode.Neq)
        return op2 == BinaryExpr.Opcode.Eq;
      if (op1 == op2) 
        return true;
      if (op1 == BinaryExpr.Opcode.Iff || op1 == BinaryExpr.Opcode.Imp || op2 == BinaryExpr.Opcode.Iff || op2 == BinaryExpr.Opcode.Imp)
        return op2 == BinaryExpr.Opcode.Eq ||
          (op1 == BinaryExpr.Opcode.Imp && op2 == BinaryExpr.Opcode.Iff) ||
          (op1 == BinaryExpr.Opcode.Eq && op2 == BinaryExpr.Opcode.Iff);
      return op2 == BinaryExpr.Opcode.Eq ||
        (op1 == BinaryExpr.Opcode.Lt && op2 == BinaryExpr.Opcode.Le) ||
        (op1 == BinaryExpr.Opcode.Gt && op2 == BinaryExpr.Opcode.Ge);
    }

    /// <summary>
    /// Resulting operator x op z if x op1 y op2 z.
    /// (Least upper bound in the Subsumes order).
    /// Returns null if neither of op1 or op2 subsumes the other.
    /// </summary>
    [Pure]
    public static BinaryExpr.Opcode? ResultOp(BinaryExpr.Opcode op1, BinaryExpr.Opcode op2) {
      Contract.Requires(ValidOp(op1) && ValidOp(op2));
      Contract.Ensures(Contract.Result<BinaryExpr.Opcode?>() == null || ValidOp((BinaryExpr.Opcode)Contract.Result<BinaryExpr.Opcode?>()));
      if (Subsumes(op1, op2)) {
        return op1;
      } else if (Subsumes(op2, op1)) {
        return op2;
      }
      return null;
    }
  }

  public class MatchStmt : Statement
  {
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Source != null);
      Contract.Invariant(cce.NonNullElements(Cases));
      Contract.Invariant(cce.NonNullElements(MissingCases));
    }

    public readonly Expression Source;
    public readonly List<MatchCaseStmt/*!*/>/*!*/ Cases;
    public readonly List<DatatypeCtor/*!*/> MissingCases = new List<DatatypeCtor>();  // filled in during resolution

    public MatchStmt(IToken tok, Expression source, [Captured] List<MatchCaseStmt/*!*/>/*!*/ cases)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(source != null);
      Contract.Requires(cce.NonNullElements(cases));
      this.Source = source;
      this.Cases = cases;
    }

    public override IEnumerable<Statement> SubStatements {
      get {
        foreach (var kase in Cases) {
          foreach (var s in kase.Body) {
            yield return s;
          }
        }
      }
    }
    public override IEnumerable<Expression> SubExpressions {
      get {
        yield return Source;
      }
    }
  }

  public class MatchCaseStmt : MatchCase
  {
    public readonly List<Statement/*!*/>/*!*/ Body;

    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(cce.NonNullElements(Body));
    }

    public MatchCaseStmt(IToken tok, string id, [Captured] List<BoundVar/*!*/>/*!*/ arguments, [Captured] List<Statement/*!*/>/*!*/ body)
      : base(tok, id, arguments)
    {
      Contract.Requires(tok != null);
      Contract.Requires(id != null);
      Contract.Requires(cce.NonNullElements(arguments));
      Contract.Requires(cce.NonNullElements(body));
      this.Body = body;
    }
  }

  /// <summary>
  /// The class represents several possible scenarios:
  /// * ...;
  ///   S == null
  /// * assert ...
  ///   ConditionOmitted == true
  /// * assume ...
  ///   ConditionOmitted == true
  /// * if ... { Stmt }
  ///   if ... { Stmt } else ElseStmt
  ///   ConditionOmitted == true
  /// * while ... invariant J;
  ///   ConditionOmitted == true && BodyOmitted == true
  /// * while ... invariant J; { Stmt }
  ///   ConditionOmitted == true && BodyOmitted == false
  /// </summary>
  public class SkeletonStatement : Statement
  {
    public readonly Statement S;
    public readonly bool ConditionOmitted;
    public readonly bool BodyOmitted;
    public readonly List<IToken> NameReplacements;
    public readonly List<Expression> ExprReplacements;
    public SkeletonStatement(IToken tok)
      : base(tok)
    {
      Contract.Requires(tok != null);
      S = null;
    }
    public SkeletonStatement(Statement s, bool conditionOmitted, bool bodyOmitted)
      : base(s.Tok)
    {
      Contract.Requires(s != null);
      S = s;
      ConditionOmitted = conditionOmitted;
      BodyOmitted = bodyOmitted;
    }
    public SkeletonStatement(IToken tok, List<IToken> nameReplacements, List<Expression> exprReplacements)
      : base(tok) {
      Contract.Requires(tok != null);
      NameReplacements = nameReplacements;
      ExprReplacements = exprReplacements;
      
    }
    public override IEnumerable<Statement> SubStatements {
      get {
        // The SkeletonStatement is really a modification of its inner statement S.  Therefore,
        // we don't consider S to be a substatement.  Instead, the substatements of S are the
        // substatements of the SkeletonStatement.  In the case the SkeletonStatement modifies
        // S by omitting its body (which is true only for loops), there are no substatements.
        if (!BodyOmitted) {
          foreach (var s in S.SubStatements) {
            yield return s;
          }
        }
      }
    }
  }

  // ------------------------------------------------------------------------------------------------------

  public abstract class TokenWrapper : IToken
  {
    protected readonly IToken WrappedToken;
    protected TokenWrapper(IToken wrappedToken) {
      Contract.Requires(wrappedToken != null);
      WrappedToken = wrappedToken;
    }

    public int col {
      get { return WrappedToken.col; }
      set { throw new NotSupportedException(); }
    }
    public virtual string filename {
      get { return WrappedToken.filename; }
      set { throw new NotSupportedException(); }
    }
    public bool IsValid {
      get { return WrappedToken.IsValid; }
    }
    public int kind {
      get { return WrappedToken.kind; }
      set { throw new NotSupportedException(); }
    }
    public int line {
      get { return WrappedToken.line; }
      set { throw new NotSupportedException(); }
    }
    public int pos {
      get { return WrappedToken.pos; }
      set { throw new NotSupportedException(); }
    }
    public string val {
      get { return WrappedToken.val; }
      set { throw new NotSupportedException(); }
    }
  }

  public class NestedToken : TokenWrapper
  {
    public NestedToken(IToken outer, IToken inner)
      : base(outer)
    {
      Contract.Requires(outer != null);
      Contract.Requires(inner != null);
      Inner = inner;
    }
    public IToken Outer { get { return WrappedToken; } }
    public readonly IToken Inner;
  }

  // ------------------------------------------------------------------------------------------------------

  public abstract class Expression
  {
    public readonly IToken tok;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(tok != null);
    }

    [Pure]
    public bool WasResolved()
    {
      return Type != null;
    }

    public Expression Resolved {
      get {
        Contract.Requires(WasResolved());  // should be called only on resolved expressions; this approximates that precondition
        Expression r = this;
        while (true) {
          var rr = r as ConcreteSyntaxExpression;
          if (rr == null) { break; }
          r = rr.ResolvedExpression;
        }
        return r;
      }
    }

    protected Type type;
    public Type Type {  // filled in during resolution
      [Verify(false)]  // TODO: how do we allow Type.get to modify type and still be [Pure]?
      [Additive]  // validity of proper subclasses is not required
      get {
        Contract.Ensures(type != null || Contract.Result<Type>() == null);  // useful in conjunction with postcondition of constructor
        return type == null ? null : type.Normalize();
      }
      [NoDefaultContract]  // no particular validity of 'this' is required, except that it not be committed
      set {
        Contract.Requires(cce.IsValid(this));
        Contract.Requires(!WasResolved());  // set it only once
        Contract.Requires(value != null);
        //modifies type;
        type = value.Normalize();
      }
    }

    public Expression(IToken tok) {
      Contract.Requires(tok != null);
      Contract.Ensures(type == null);  // we would have liked to have written Type==null, but that's not admissible or provable

      this.tok = tok;
    }

    /// <summary>
    /// Returns the non-null subexpressions of the Expression.
    /// </summary>
    public virtual IEnumerable<Expression> SubExpressions {
      get { yield break; }
    }
  }

  /// <summary>
  /// Instances of this class are introduced during resolution to indicate that a static method or function has
  /// been invoked without specifying a receiver (that is, by just giving the name of the enclosing class).
  /// </summary>
  public class StaticReceiverExpr : LiteralExpr
  {
    public StaticReceiverExpr(IToken tok, ClassDecl cl)
      : base(tok)  // constructs a LiteralExpr representing the 'null' literal
    {
      Contract.Requires(tok != null);
      Contract.Requires(cl != null);
      var typeArgs = new List<Type>();
      foreach (var ta in cl.TypeArgs) {
        typeArgs.Add(new InferredTypeProxy());
      }
      Type = new UserDefinedType(tok, cl.Name, cl, typeArgs);
    }
  }

  public class LiteralExpr : Expression {
    public readonly object Value;

    [Pure]
    public static bool IsTrue(Expression e) {
      Contract.Requires(e != null);
      if (e is LiteralExpr) {
        LiteralExpr le = (LiteralExpr)e;
        return le.Value is bool && (bool)le.Value;
      } else {
        return false;
      }
    }

    public LiteralExpr(IToken tok)
      : base(tok) {  // represents the Dafny literal "null"
      Contract.Requires(tok != null);
      this.Value = null;
    }

    public LiteralExpr(IToken tok, BigInteger n)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(0 <= n.Sign);

      this.Value = n;
    }

    public LiteralExpr(IToken tok, int n) :base(tok){
      Contract.Requires(tok != null);
      Contract.Requires(0 <= n);

      this.Value = new BigInteger(n);
    }

    public LiteralExpr(IToken tok, bool b)
      : base(tok) {
      Contract.Requires(tok != null);
      this.Value = b;

    }
  }

  public class DatatypeValue : Expression {
    public readonly string DatatypeName;
    public readonly string MemberName;
    public readonly List<Expression/*!*/>/*!*/ Arguments;
    public DatatypeCtor Ctor;  // filled in by resolution
    public List<Type/*!*/> InferredTypeArgs = new List<Type>();  // filled in by resolution
    public bool IsCoCall;  // filled in by resolution
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(DatatypeName != null);
      Contract.Invariant(MemberName != null);
      Contract.Invariant(cce.NonNullElements(Arguments));
      Contract.Invariant(cce.NonNullElements(InferredTypeArgs));
    }

    public DatatypeValue(IToken tok, string datatypeName, string memberName, [Captured] List<Expression/*!*/>/*!*/ arguments)
      : base(tok) {
      Contract.Requires(cce.NonNullElements(arguments));
      Contract.Requires(tok != null);
      Contract.Requires(datatypeName != null);
      Contract.Requires(memberName != null);
      this.DatatypeName = datatypeName;
      this.MemberName = memberName;
      this.Arguments = arguments;
    }

    public override IEnumerable<Expression> SubExpressions {
      get { return Arguments; }
    }
  }

  public class ThisExpr : Expression {
    public ThisExpr(IToken tok)
      : base(tok) {
      Contract.Requires(tok != null);
    }
  }
  public class ExpressionPair {
    public Expression A, B;
    public ExpressionPair(Expression a, Expression b) {
      Contract.Requires(a != null);
      Contract.Requires(b != null);
      A = a;
      B = b;
    }
  }

  public class ImplicitThisExpr : ThisExpr {
    public ImplicitThisExpr(IToken tok)
      : base(tok) {
      Contract.Requires(tok != null);
    }
  }

  public class IdentifierExpr : Expression
  {
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Name != null);
    }

    public readonly string Name;
    public IVariable Var;  // filled in by resolution

    public IdentifierExpr(IToken tok, string name)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(name != null);
      Name = name;
    }
  }

  /// <summary>
  /// If an "AutoGhostIdentifierExpr" is used as the out-parameter of a ghost method or
  /// a method with a ghost parameter, resolution will change the .Var's .IsGhost to true
  /// automatically.  This class is intended to be used only as a communicate between the
  /// parser and parts of the resolver.
  /// </summary>
  public class AutoGhostIdentifierExpr : IdentifierExpr
  {
    public AutoGhostIdentifierExpr(IToken tok, string name)
      : base(tok, name) { }
  }

  public abstract class DisplayExpression : Expression {
    public readonly List<Expression/*!*/>/*!*/ Elements;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(cce.NonNullElements(Elements));
    }

    public DisplayExpression(IToken tok, List<Expression/*!*/>/*!*/ elements)
      : base(tok) {
      Contract.Requires(cce.NonNullElements(elements));
      Elements = elements;
    }

    public override IEnumerable<Expression> SubExpressions {
      get { return Elements; }
    }
  }

  public class SetDisplayExpr : DisplayExpression {
    public SetDisplayExpr(IToken tok, List<Expression/*!*/>/*!*/ elements)
      : base(tok, elements) {
      Contract.Requires(tok != null);
      Contract.Requires(cce.NonNullElements(elements));
    }
  }
  
  public class MultiSetDisplayExpr : DisplayExpression {
    public MultiSetDisplayExpr(IToken tok, List<Expression/*!*/>/*!*/ elements) : base(tok, elements) {
      Contract.Requires(tok != null);
      Contract.Requires(cce.NonNullElements(elements));
    }
  }

  public class MapDisplayExpr : Expression {
    public List<ExpressionPair/*!*/>/*!*/ Elements;
    public MapDisplayExpr(IToken tok, List<ExpressionPair/*!*/>/*!*/ elements)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(cce.NonNullElements(elements));
      Elements = elements;
    }
  }
  public class SeqDisplayExpr : DisplayExpression {
    public SeqDisplayExpr(IToken tok, List<Expression/*!*/>/*!*/ elements)
      : base(tok, elements) {
      Contract.Requires(cce.NonNullElements(elements));
      Contract.Requires(tok != null);
    }
  }

  public class FieldSelectExpr : Expression {
    public readonly Expression Obj;
    public readonly string FieldName;
    public Field Field;  // filled in by resolution

    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Obj != null);
      Contract.Invariant(FieldName != null);
    }

    public FieldSelectExpr(IToken tok, Expression obj, string fieldName)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(obj != null);
      Contract.Requires(fieldName != null);
      this.Obj = obj;
      this.FieldName = fieldName;
    }

    public override IEnumerable<Expression> SubExpressions {
      get { yield return Obj; }
    }
  }

  public class SeqSelectExpr : Expression {
    public readonly bool SelectOne;  // false means select a range
    public readonly Expression Seq;
    public readonly Expression E0;
    public readonly Expression E1;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Seq != null);
      Contract.Invariant(!SelectOne || E1 == null);
    }

    public SeqSelectExpr(IToken tok, bool selectOne, Expression seq, Expression e0, Expression e1)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(seq != null);
      Contract.Requires(!selectOne || e1 == null);

      SelectOne = selectOne;
      Seq = seq;
      E0 = e0;
      E1 = e1;
    }

    public override IEnumerable<Expression> SubExpressions {
      get {
        yield return Seq;
        if (E0 != null) yield return E0;
        if (E1 != null) yield return E1;
      }
    }
  }

  public class MultiSelectExpr : Expression {
    public readonly Expression Array;
    public readonly List<Expression> Indices;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Array != null);
      Contract.Invariant(cce.NonNullElements(Indices));
      Contract.Invariant(1 <= Indices.Count);
    }

    public MultiSelectExpr(IToken tok, Expression array, List<Expression> indices)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(array != null);
      Contract.Requires(cce.NonNullElements(indices) && 1 <= indices.Count);

      Array = array;
      Indices = indices;
    }

    public override IEnumerable<Expression> SubExpressions {
      get {
        yield return Array;
        foreach (var e in Indices) {
          yield return e;
        }
      }
    }
  }

  public class SeqUpdateExpr : Expression {
    public readonly Expression Seq;
    public readonly Expression Index;
    public readonly Expression Value;

    public SeqUpdateExpr(IToken tok, Expression seq, Expression index, Expression val)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(seq != null);
      Contract.Requires(index != null);
      Contract.Requires(val != null);
      Seq = seq;
      Index = index;
      Value = val;
    }

    public override IEnumerable<Expression> SubExpressions {
      get {
        yield return Seq;
        yield return Index;
        yield return Value;
      }
    }
  }

  public class FunctionCallExpr : Expression {
    public readonly string/*!*/ Name;
    public readonly Expression/*!*/ Receiver;
    public readonly IToken OpenParen;  // can be null if Args.Count == 0
    public readonly List<Expression/*!*/>/*!*/ Args;
    public Dictionary<TypeParameter, Type> TypeArgumentSubstitutions;  // created, initialized, and used by resolution (could be deleted once all of resolution is done)
    public enum CoCallResolution { No, Yes, NoBecauseFunctionHasSideEffects, NoBecauseRecursiveCallsAreNotAllowedInThisContext, NoBecauseIsNotGuarded }
    public CoCallResolution CoCall = CoCallResolution.No;  // indicates whether or not the call is a co-recursive call; filled in by resolution

    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Name != null);
      Contract.Invariant(Receiver != null);
      Contract.Invariant(cce.NonNullElements(Args));
    }

    public Function Function;  // filled in by resolution

    [Captured]
    public FunctionCallExpr(IToken tok, string fn, Expression receiver, IToken openParen, [Captured] List<Expression/*!*/>/*!*/ args)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(fn != null);
      Contract.Requires(receiver != null);
      Contract.Requires(cce.NonNullElements(args));
      Contract.Requires(openParen != null || args.Count == 0);
      Contract.Ensures(type == null);
      Contract.Ensures(cce.Owner.Same(this, receiver));

      this.Name = fn;
      cce.Owner.AssignSame(this, receiver);
      this.Receiver = receiver;
      this.OpenParen = openParen;
      this.Args = args;
    }

    public override IEnumerable<Expression> SubExpressions {
      get {
        if (!Function.IsStatic) {
          yield return Receiver;
        }
        foreach (var e in Args) {
          yield return e;
        }
      }
    }
  }

  public class OldExpr : Expression {
    [Peer]
    public readonly Expression E;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(E != null);
    }

    [Captured]
    public OldExpr(IToken tok, Expression expr)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(expr != null);
      cce.Owner.AssignSame(this, expr);
      E = expr;
    }

    public override IEnumerable<Expression> SubExpressions {
      get { yield return E; }
    }
  }

  public class MultiSetFormingExpr : Expression
  {
    [Peer]
    public readonly Expression E;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(E != null);
    }

    [Captured]
    public MultiSetFormingExpr(IToken tok, Expression expr)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(expr != null);
      cce.Owner.AssignSame(this, expr);
      E = expr;
    }

    public override IEnumerable<Expression> SubExpressions {
      get { yield return E; }
    }
  }
  public class FreshExpr : Expression {
    public readonly Expression E;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(E != null);
    }

    public FreshExpr(IToken tok, Expression expr)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(expr != null);
      E = expr;
    }

    public override IEnumerable<Expression> SubExpressions {
      get { yield return E; }
    }
  }

  public class UnaryExpr : Expression
  {
    public enum Opcode {
      Not,
      SetChoose,  // Important: SetChoose is not a function, so it can only be used in a statement context (in particular, the RHS of an assignment)
      SeqLength
    }
    public readonly Opcode Op;
    public readonly Expression E;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(E != null);
    }

    public UnaryExpr(IToken tok, Opcode op, Expression e)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(e != null);
      this.Op = op;
      this.E = e;

    }

    public override IEnumerable<Expression> SubExpressions {
      get { yield return E; }
    }
  }

  public class BinaryExpr : Expression {
    public enum Opcode {
      Iff,
      Imp,
      And,
      Or,
      Eq,
      Neq,
      Lt,
      Le,
      Ge,
      Gt,
      Disjoint,
      In,
      NotIn,
      Add,
      Sub,
      Mul,
      Div,
      Mod
    }
    public readonly Opcode Op;
    public enum ResolvedOpcode {
      // logical operators
      Iff,
      Imp,
      And,
      Or,
      // non-collection types
      EqCommon,
      NeqCommon,
      // integers
      Lt,
      Le,
      Ge,
      Gt,
      Add,
      Sub,
      Mul,
      Div,
      Mod,
      // sets
      SetEq,
      SetNeq,
      ProperSubset,
      Subset,
      Superset,
      ProperSuperset,
      Disjoint,
      InSet,
      NotInSet,
      Union,
      Intersection,
      SetDifference,
      // multi-sets
      MultiSetEq,
      MultiSetNeq,
      MultiSubset,
      MultiSuperset,
      ProperMultiSubset,
      ProperMultiSuperset,
      MultiSetDisjoint,
      InMultiSet,
      NotInMultiSet,
      MultiSetUnion,
      MultiSetIntersection,
      MultiSetDifference,
      // Sequences
      SeqEq,
      SeqNeq,
      ProperPrefix,
      Prefix,
      Concat,
      InSeq,
      NotInSeq,
      // Maps
      MapEq,
      MapNeq,
      InMap,
      NotInMap,
      MapDisjoint,
      MapUnion,
      // datatypes
      RankLt,
      RankGt
    }
    public ResolvedOpcode ResolvedOp;  // filled in by resolution

    public static Opcode ResolvedOp2SyntacticOp(ResolvedOpcode rop) {
      switch (rop) {
        case ResolvedOpcode.Iff: return Opcode.Iff;
        case ResolvedOpcode.Imp: return Opcode.Imp;
        case ResolvedOpcode.And: return Opcode.And;
        case ResolvedOpcode.Or: return Opcode.Or;

        case ResolvedOpcode.EqCommon:
        case ResolvedOpcode.SetEq:
        case ResolvedOpcode.MultiSetEq:
        case ResolvedOpcode.SeqEq:
        case ResolvedOpcode.MapEq:
          return Opcode.Eq;

        case ResolvedOpcode.NeqCommon:
        case ResolvedOpcode.SetNeq:
        case ResolvedOpcode.MultiSetNeq:
        case ResolvedOpcode.SeqNeq:
        case ResolvedOpcode.MapNeq:
          return Opcode.Neq;

        case ResolvedOpcode.Lt:
        case ResolvedOpcode.ProperSubset:
        case ResolvedOpcode.ProperMultiSuperset:
        case ResolvedOpcode.ProperPrefix:
        case ResolvedOpcode.RankLt:
          return Opcode.Lt;

        case ResolvedOpcode.Le:
        case ResolvedOpcode.Subset:
        case ResolvedOpcode.MultiSubset:
        case ResolvedOpcode.Prefix:
          return Opcode.Le;

        case ResolvedOpcode.Ge:
        case ResolvedOpcode.Superset:
        case ResolvedOpcode.MultiSuperset:
          return Opcode.Ge;

        case ResolvedOpcode.Gt:
        case ResolvedOpcode.ProperSuperset:
        case ResolvedOpcode.ProperMultiSubset:
        case ResolvedOpcode.RankGt:
          return Opcode.Gt;

        case ResolvedOpcode.Add:
        case ResolvedOpcode.Union:
        case ResolvedOpcode.MultiSetUnion:
        case ResolvedOpcode.MapUnion:
        case ResolvedOpcode.Concat:
          return Opcode.Add;

        case ResolvedOpcode.Sub:
        case ResolvedOpcode.SetDifference:
        case ResolvedOpcode.MultiSetDifference:
          return Opcode.Sub;

        case ResolvedOpcode.Mul:
        case ResolvedOpcode.Intersection:
        case ResolvedOpcode.MultiSetIntersection:
          return Opcode.Mul;

        case ResolvedOpcode.Div: return Opcode.Div;
        case ResolvedOpcode.Mod: return Opcode.Mod;

        case ResolvedOpcode.Disjoint:
        case ResolvedOpcode.MultiSetDisjoint:
        case ResolvedOpcode.MapDisjoint:
          return Opcode.Disjoint;

        case ResolvedOpcode.InSet:
        case ResolvedOpcode.InMultiSet:
        case ResolvedOpcode.InSeq:
        case ResolvedOpcode.InMap:
          return Opcode.In;

        case ResolvedOpcode.NotInSet:
        case ResolvedOpcode.NotInMultiSet:
        case ResolvedOpcode.NotInSeq:
        case ResolvedOpcode.NotInMap:
          return Opcode.NotIn;

        default:
          Contract.Assert(false);  // unexpected ResolvedOpcode
          return Opcode.Add;  // please compiler
      }
    }

    public static string OpcodeString(Opcode op) {
      Contract.Ensures(Contract.Result<string>() != null);

      switch (op) {
        case Opcode.Iff:
          return "<==>";
        case Opcode.Imp:
          return "==>";
        case Opcode.And:
          return "&&";
        case Opcode.Or:
          return "||";
        case Opcode.Eq:
          return "==";
        case Opcode.Lt:
          return "<";
        case Opcode.Gt:
          return ">";
        case Opcode.Le:
          return "<=";
        case Opcode.Ge:
          return ">=";
        case Opcode.Neq:
          return "!=";
        case Opcode.Disjoint:
          return "!!";
        case Opcode.In:
          return "in";
        case Opcode.NotIn:
          return "!in";
        case Opcode.Add:
          return "+";
        case Opcode.Sub:
          return "-";
        case Opcode.Mul:
          return "*";
        case Opcode.Div:
          return "/";
        case Opcode.Mod:
          return "%";
        default:
          Contract.Assert(false);
          throw new cce.UnreachableException();  // unexpected operator
      }
    }
    public readonly Expression E0;
    public readonly Expression E1;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(E0 != null);
      Contract.Invariant(E1 != null);
    }


    public BinaryExpr(IToken tok, Opcode op, Expression e0, Expression e1)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(e0 != null);
      Contract.Requires(e1 != null);
      this.Op = op;
      this.E0 = e0;
      this.E1 = e1;
    }

    public override IEnumerable<Expression> SubExpressions {
      get {
        yield return E0;
        yield return E1;
      }
    }
  }

  public class LetExpr : Expression
  {
    public readonly List<BoundVar> Vars;
    public readonly List<Expression> RHSs;
    public readonly Expression Body;
    public LetExpr(IToken tok, List<BoundVar> vars, List<Expression> rhss, Expression body)
      : base(tok) {
      Vars = vars;
      RHSs = rhss;
      Body = body;
    }
    public override IEnumerable<Expression> SubExpressions {
      get {
        foreach (var rhs in RHSs) {
          yield return rhs;
        }
        yield return Body;
      }
    }
  }
  // Represents expr Name: Body
  //         or expr Name: (assert Body == Contract; Body)
  public class NamedExpr : Expression
  {
    public readonly string Name;
    public readonly Expression Body;
    public readonly Expression Contract;
    public readonly IToken ReplacerToken;

    public NamedExpr(IToken tok, string p, Expression body)
      : base(tok) {
      Name = p;
      Body = body;
    }
    public NamedExpr(IToken tok, string p, Expression body, Expression contract, IToken token)
      : base(tok) {
      Name = p;
      Body = body;
      Contract = contract;
      ReplacerToken = token;
    }
    public override IEnumerable<Expression> SubExpressions {
      get {
        yield return Body;
        if (Contract != null) yield return Contract;
      }
    }
  }

  /// <summary>
  /// A ComprehensionExpr has the form:
  ///   BINDER x Attributes | Range(x) :: Term(x)
  /// When BINDER is "forall" or "exists", the range may be "null" (which stands for the logical value "true").
  /// For other BINDERs (currently, "set"), the range is non-null.
  /// where "Attributes" is optional, and "| Range(x)" is optional and defaults to "true".
  /// Currently, BINDER is one of the logical quantifiers "exists" or "forall".
  /// </summary>
  public abstract class ComprehensionExpr : Expression {
    public readonly List<BoundVar/*!*/>/*!*/ BoundVars;
    public readonly Expression Range;
    public readonly Expression/*!*/ Term;

    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(BoundVars != null);
      Contract.Invariant(Term != null);
    }

    public readonly Attributes Attributes;

    public abstract class BoundedPool { }
    public class IntBoundedPool : BoundedPool
    {
      public readonly Expression LowerBound;
      public readonly Expression UpperBound;
      public IntBoundedPool(Expression lowerBound, Expression upperBound) {
        LowerBound = lowerBound;
        UpperBound = upperBound;
      }
    }
    public class SetBoundedPool : BoundedPool
    {
      public readonly Expression Set;
      public SetBoundedPool(Expression set) { Set = set; }
    }
    public class SuperSetBoundedPool : BoundedPool
    {
      public readonly Expression LowerBound;
      public SuperSetBoundedPool(Expression set) { LowerBound = set; }
    }
    public class MapBoundedPool : BoundedPool
    {
      public readonly Expression Map;
      public MapBoundedPool(Expression map) { Map = map; }
    }
    public class SeqBoundedPool : BoundedPool
    {
      public readonly Expression Seq;
      public SeqBoundedPool(Expression seq) { Seq = seq; }
    }
    public class BoolBoundedPool : BoundedPool
    {
    }
    public class DatatypeBoundedPool : BoundedPool
    {
      public readonly DatatypeDecl Decl;
      public DatatypeBoundedPool(DatatypeDecl d) { Decl = d; }
    }

    public List<BoundedPool> Bounds;  // initialized and filled in by resolver
    // invariant Bounds == null || Bounds.Count == BoundVars.Count;
    public List<BoundVar> MissingBounds;  // filled in during resolution; remains "null" if bounds can be found
    // invariant Bounds == null || MissingBounds == null;

    public ComprehensionExpr(IToken/*!*/ tok, List<BoundVar/*!*/>/*!*/ bvars, Expression range, Expression/*!*/ term, Attributes attrs)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(cce.NonNullElements(bvars));
      Contract.Requires(term != null);

      this.BoundVars = bvars;
      this.Range = range;
      this.Term = term;
      this.Attributes = attrs;
    }

    public override IEnumerable<Expression> SubExpressions {
      get {
        if (Range != null) { yield return Range; }
        yield return Term;
      }
    }
  }

  public abstract class QuantifierExpr : ComprehensionExpr {
    public QuantifierExpr(IToken/*!*/ tok, List<BoundVar/*!*/>/*!*/ bvars, Expression range, Expression/*!*/ term, Attributes attrs)
      : base(tok, bvars, range, term, attrs) {
      Contract.Requires(tok != null);
      Contract.Requires(cce.NonNullElements(bvars));
      Contract.Requires(term != null);
    }
    public abstract Expression/*!*/ LogicalBody();
  }

  public class ForallExpr : QuantifierExpr {
    public ForallExpr(IToken tok, List<BoundVar/*!*/>/*!*/ bvars, Expression range, Expression term, Attributes attrs)
      : base(tok, bvars, range, term, attrs) {
      Contract.Requires(cce.NonNullElements(bvars));
      Contract.Requires(tok != null);
      Contract.Requires(term != null);
    }
    public override Expression/*!*/ LogicalBody() {
      if (Range == null) {
        return Term;
      }
      var body = new BinaryExpr(Term.tok, BinaryExpr.Opcode.Imp, Range, Term);
      body.ResolvedOp = BinaryExpr.ResolvedOpcode.Imp;
      body.Type = Term.Type;
      return body;
    }
  }

  public class ExistsExpr : QuantifierExpr {
    public ExistsExpr(IToken tok, List<BoundVar/*!*/>/*!*/ bvars, Expression range, Expression term, Attributes attrs)
      : base(tok, bvars, range, term, attrs) {
      Contract.Requires(cce.NonNullElements(bvars));
      Contract.Requires(tok != null);
      Contract.Requires(term != null);
    }
    public override Expression/*!*/ LogicalBody() {
      if (Range == null) {
        return Term;
      }
      var body = new BinaryExpr(Term.tok, BinaryExpr.Opcode.And, Range, Term);
      body.ResolvedOp = BinaryExpr.ResolvedOpcode.And;
      body.Type = Term.Type;
      return body;
    }
  }

  public class SetComprehension : ComprehensionExpr
  {
    public readonly bool TermIsImplicit;

    public SetComprehension(IToken/*!*/ tok, List<BoundVar/*!*/>/*!*/ bvars, Expression/*!*/ range, Expression term)
      : base(tok, bvars, range, term ?? new IdentifierExpr(tok, bvars[0].Name), null) {
      Contract.Requires(tok != null);
      Contract.Requires(cce.NonNullElements(bvars));
      Contract.Requires(1 <= bvars.Count);
      Contract.Requires(range != null);

      TermIsImplicit = term == null;
    }
  }
  public class MapComprehension : ComprehensionExpr
  {
    public MapComprehension(IToken/*!*/ tok, List<BoundVar/*!*/>/*!*/ bvars, Expression/*!*/ range, Expression term)
      : base(tok, bvars, range, term, null) {
      Contract.Requires(tok != null);
      Contract.Requires(cce.NonNullElements(bvars));
      Contract.Requires(1 <= bvars.Count);
      Contract.Requires(range != null);
      Contract.Requires(term != null);
    }
  }

  public class WildcardExpr : Expression
  {  // a WildcardExpr can occur only in reads clauses and a loop's decreases clauses (with different meanings)
    public WildcardExpr(IToken tok)
      : base(tok) {
      Contract.Requires(tok != null);
    }
  }

  public abstract class PredicateExpr : Expression
  {
    public readonly Expression Guard;
    public readonly Expression Body;
    public PredicateExpr(IToken tok, Expression guard, Expression body)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(guard != null);
      Contract.Requires(body != null);
      Guard = guard;
      Body = body;
    }
    public abstract string Kind { get; }

    public override IEnumerable<Expression> SubExpressions {
      get {
        yield return Guard;
        yield return Body;
      }
    }
  }

  public class AssertExpr : PredicateExpr
  {
    public AssertExpr(IToken tok, Expression guard, Expression body)
      : base(tok, guard, body) {
      Contract.Requires(tok != null);
      Contract.Requires(guard != null);
      Contract.Requires(body != null);
    }
    public override string Kind { get { return "assert"; } }
  }

  public class AssumeExpr : PredicateExpr
  {
    public AssumeExpr(IToken tok, Expression guard, Expression body)
      : base(tok, guard, body) {
      Contract.Requires(tok != null);
      Contract.Requires(guard != null);
      Contract.Requires(body != null);
    }
    public override string Kind { get { return "assume"; } }
  }

  public class ITEExpr : Expression
  {
    public readonly Expression Test;
    public readonly Expression Thn;
    public readonly Expression Els;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Test != null);
      Contract.Invariant(Thn != null);
      Contract.Invariant(Els != null);
    }

    public ITEExpr(IToken tok, Expression test, Expression thn, Expression els)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(test != null);
      Contract.Requires(thn != null);
      Contract.Requires(els != null);
      this.Test = test;
      this.Thn = thn;
      this.Els = els;
    }

    public override IEnumerable<Expression> SubExpressions {
      get {
        yield return Test;
        yield return Thn;
        yield return Els;
      }
    }
  }

  public class MatchExpr : Expression {  // a MatchExpr is an "extended expression" and is only allowed in certain places
    public readonly Expression Source;
    public readonly List<MatchCaseExpr/*!*/>/*!*/ Cases;
    public readonly List<DatatypeCtor/*!*/> MissingCases = new List<DatatypeCtor>();  // filled in during resolution

    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Source != null);
      Contract.Invariant(cce.NonNullElements(Cases));
      Contract.Invariant(cce.NonNullElements(MissingCases));
    }

    public MatchExpr(IToken tok, Expression source, [Captured] List<MatchCaseExpr/*!*/>/*!*/ cases)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(source != null);
      Contract.Requires(cce.NonNullElements(cases));
      this.Source = source;
      this.Cases = cases;
    }

    public override IEnumerable<Expression> SubExpressions {
      get {
        yield return Source;
        foreach (var mc in Cases) {
          yield return mc.Body;
        }
      }
    }
  }

  public abstract class MatchCase
  {
    public readonly IToken tok;
    public readonly string Id;
    public DatatypeCtor Ctor;  // filled in by resolution
    public readonly List<BoundVar/*!*/>/*!*/ Arguments;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(tok != null);
      Contract.Invariant(Id != null);
      Contract.Invariant(cce.NonNullElements(Arguments));
    }

    public MatchCase(IToken tok, string id, [Captured] List<BoundVar/*!*/>/*!*/ arguments) {
      Contract.Requires(tok != null);
      Contract.Requires(id != null);
      Contract.Requires(cce.NonNullElements(arguments));
      this.tok = tok;
      this.Id = id;
      this.Arguments = arguments;
    }
  }

  public class MatchCaseExpr : MatchCase
  {
    public readonly Expression/*!*/ Body;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Body != null);
    }

    public MatchCaseExpr(IToken tok, string id, [Captured] List<BoundVar/*!*/>/*!*/ arguments, Expression body)
      : base(tok, id, arguments)
    {
      Contract.Requires(tok != null);
      Contract.Requires(id != null);
      Contract.Requires(cce.NonNullElements(arguments));
      Contract.Requires(body != null);
      this.Body = body;
    }
  }

  public class BoxingCastExpr : Expression {  // a BoxingCastExpr is used only as a temporary placeholding during translation
    public readonly Expression E;
    public readonly Type FromType;
    public readonly Type ToType;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(E != null);
      Contract.Invariant(FromType != null);
      Contract.Invariant(ToType != null);
    }

    public BoxingCastExpr(Expression e, Type fromType, Type toType)
      : base(e.tok) {
      Contract.Requires(e != null);
      Contract.Requires(fromType != null);
      Contract.Requires(toType != null);

      E = e;
      FromType = fromType;
      ToType = toType;
    }

    public override IEnumerable<Expression> SubExpressions {
      get { yield return E; }
    }
  }

  public class UnboxingCastExpr : Expression {  // an UnboxingCastExpr is used only as a temporary placeholding during translation
    public readonly Expression E;
    public readonly Type FromType;
    public readonly Type ToType;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(E != null);
      Contract.Invariant(FromType != null);
      Contract.Invariant(ToType != null);
    }

    public UnboxingCastExpr(Expression e, Type fromType, Type toType)
      : base(e.tok) {
      Contract.Requires(e != null);
      Contract.Requires(fromType != null);
      Contract.Requires(toType != null);

      E = e;
      FromType = fromType;
      ToType = toType;
    }

    public override IEnumerable<Expression> SubExpressions {
      get { yield return E; }
    }
  }


  public class MaybeFreeExpression {
    public readonly Expression E;
    public readonly bool IsFree;
    
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(E != null);
    }

    private Attributes attributes;
    public Attributes Attributes {
      get {
        return attributes;
      }
      set {
        attributes = value;
      }
    }

    public bool HasAttributes() {
      return Attributes != null;
    }

    public MaybeFreeExpression(Expression e)
      : this(e, false, null)
    {
      Contract.Requires(e != null);
    }

    public MaybeFreeExpression(Expression e, bool isFree)
      : this(e, isFree, null)
    {
      Contract.Requires(e != null);
    }

    public MaybeFreeExpression(Expression e, bool isFree, Attributes attrs) {
      Contract.Requires(e != null);
      E = e;
      IsFree = isFree;
      Attributes = attrs;
    }
  }


  public class FrameExpression {
    public readonly IToken tok;
    public readonly Expression E;  // may be a WildcardExpr
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(E != null);
      Contract.Invariant(!(E is WildcardExpr) || FieldName == null && Field == null);
    }

    public readonly string FieldName;
    public Field Field;  // filled in during resolution (but is null if FieldName is)

    /// <summary>
    /// If a "fieldName" is given, then "tok" denotes its source location.  Otherwise, "tok"
    /// denotes the source location of "e".
    /// </summary>
    public FrameExpression(IToken tok, Expression e, string fieldName) {
      Contract.Requires(tok != null);
      Contract.Requires(e != null);
      Contract.Requires(!(e is WildcardExpr) || fieldName == null);
      this.tok = tok;
      E = e;
      FieldName = fieldName;
    }
  }

  /// <summary>
  /// This class represents a piece of concrete syntax in the parse tree.  During resolution,
  /// it gets "replaced" by the expression in "ResolvedExpression".
  /// </summary>
  public abstract class ConcreteSyntaxExpression : Expression
  {
    public Expression ResolvedExpression;  // filled in during resolution; after resolution, manipulation of "this" should proceed as with manipulating "this.ResolvedExpression"
    public ConcreteSyntaxExpression(IToken tok)
      : base(tok) {
    }
    public override IEnumerable<Expression> SubExpressions {
      get {
        if (ResolvedExpression != null) {
          yield return ResolvedExpression;
        }
      }
    }
  }

  public class ParensExpression : ConcreteSyntaxExpression
  {
    public readonly Expression E;
    public ParensExpression(IToken tok, Expression e)
      : base(tok) {
      E = e;
    }
  }

  public class ChainingExpression : ConcreteSyntaxExpression
  {
    public readonly List<Expression> Operands;
    public readonly List<BinaryExpr.Opcode> Operators;
    public readonly Expression E;
    public ChainingExpression(IToken tok, List<Expression> operands, List<BinaryExpr.Opcode> operators, Expression desugaring)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(operands != null);
      Contract.Requires(operators != null);
      Contract.Requires(desugaring != null);
      Contract.Requires(operands.Count == operators.Count + 1);

      Operands = operands;
      Operators = operators;
      E = desugaring;
    }
  }

  /// <summary>
  /// An ExprDotName desugars into either a FieldSelectExpr or a FunctionCallExpr (with a parameterless predicate function).
  /// </summary>
  public class ExprDotName : ConcreteSyntaxExpression
  {
    public readonly Expression Obj;
    public readonly string SuffixName;

    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Obj != null);
      Contract.Invariant(SuffixName != null);
    }

    public ExprDotName(IToken tok, Expression obj, string suffixName)
      : base(tok) {
      Contract.Requires(tok != null);
      Contract.Requires(obj != null);
      Contract.Requires(suffixName != null);
      this.Obj = obj;
      this.SuffixName = suffixName;
    }
  }

  public class IdentifierSequence : ConcreteSyntaxExpression
  {
    public readonly List<IToken> Tokens;
    public readonly IToken OpenParen;
    public readonly List<Expression> Arguments;
    public IdentifierSequence(List<IToken> tokens, IToken openParen, List<Expression> args)
      : base(tokens[0]) {
      Contract.Requires(tokens != null && 1 <= tokens.Count);
      /* "args" is null to indicate the absence of a parenthesized suffix */
      Contract.Requires(args == null || openParen != null);

      Tokens = tokens;
      OpenParen = openParen;
      Arguments = args;
    }
  }


  public class Specification<T> where T : class
  {
    public readonly List<T> Expressions;

    [ContractInvariantMethod]
    private void ObjectInvariant()
    {
      Contract.Invariant(Expressions == null || cce.NonNullElements<T>(Expressions));
    }


    public Specification(List<T> exprs, Attributes attrs)
    {
      Contract.Requires(exprs == null || cce.NonNullElements<T>(exprs));
      Expressions = exprs;
      Attributes = attrs;
    }

    private Attributes attributes;
    public Attributes Attributes
    {
      get
      {
        return attributes;
      }
      set
      {
        attributes = value;
      }
    }

    public bool HasAttributes()
    {
      return Attributes != null;
    }
  }
  public abstract class TranslationTask
  {

  }
  public class MethodCheck : TranslationTask
  {
    public readonly Method Refined;
    public readonly Method Refining;
    public MethodCheck(Method a, Method b) {
      Refined = b;
      Refining = a;
    }
  }
  public class FunctionCheck : TranslationTask
  {
    public readonly Function Refined;
    public readonly Function Refining;
    public FunctionCheck(Function a, Function b) {
      Refined = b;
      Refining = a;
    }
  }
}