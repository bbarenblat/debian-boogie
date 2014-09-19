//-----------------------------------------------------------------------------
//
// Copyright (C) Microsoft Corporation.  All Rights Reserved.
//
//-----------------------------------------------------------------------------
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.IO;
//using ExternalProver;
using System.Linq;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using Microsoft.Boogie.AbstractInterpretation;
using Microsoft.Boogie;
using Microsoft.Boogie.VCExprAST;
using Microsoft.Boogie.Clustering;
using Microsoft.Boogie.TypeErasure;
using System.Text;

using RPFP = Microsoft.Boogie.RPFP;

namespace Microsoft.Boogie.SMTLib
{
  public class SMTLibProcessTheoremProver : ProverInterface
  {
    private readonly SMTLibProverContext ctx;
    private VCExpressionGenerator gen;
    private readonly SMTLibProverOptions options;
    private bool usingUnsatCore;
    private RPFP rpfp = null;

    [ContractInvariantMethod]
    void ObjectInvariant()
    {
      Contract.Invariant(ctx != null);
      Contract.Invariant(AxBuilder != null);
      Contract.Invariant(Namer != null);
      Contract.Invariant(DeclCollector != null);
      Contract.Invariant(cce.NonNullElements(Axioms));
      Contract.Invariant(cce.NonNullElements(TypeDecls));
      Contract.Invariant(_backgroundPredicates != null);

    }


    [NotDelayed]
    public SMTLibProcessTheoremProver(ProverOptions options, VCExpressionGenerator gen,
                                      SMTLibProverContext ctx)
    {
      Contract.Requires(options != null);
      Contract.Requires(gen != null);
      Contract.Requires(ctx != null);
      
      InitializeGlobalInformation();
      
      this.options = (SMTLibProverOptions)options;
      this.ctx = ctx;
      this.gen = gen;
      this.usingUnsatCore = false;

      SetupAxiomBuilder(gen);

      Namer = new SMTLibNamer();
      ctx.parent = this;
      this.DeclCollector = new TypeDeclCollector((SMTLibProverOptions)options, Namer);

      if (CommandLineOptions.Clo.PrintFixedPoint != null || CommandLineOptions.Clo.PrintConjectures != null)
      {
          declHandler = new MyDeclHandler();
          DeclCollector.SetDeclHandler(declHandler);
      }

      SetupProcess();

      if (CommandLineOptions.Clo.StratifiedInlining > 0 || CommandLineOptions.Clo.ContractInfer)
      {
          // Prepare for ApiChecker usage
          if (options.LogFilename != null && currentLogFile == null)
          {
              currentLogFile = OpenOutputFile("");
          }
          if (CommandLineOptions.Clo.ContractInfer && CommandLineOptions.Clo.ExplainHoudini)
          {
              SendThisVC("(set-option :produce-unsat-cores true)");
              this.usingUnsatCore = true;
          }
          PrepareCommon();
      }
    }

    private void SetupAxiomBuilder(VCExpressionGenerator gen)
    {
      switch (CommandLineOptions.Clo.TypeEncodingMethod)
      {
        case CommandLineOptions.TypeEncoding.Arguments:
          AxBuilder = new TypeAxiomBuilderArguments(gen);
          AxBuilder.Setup();
          break;
        case CommandLineOptions.TypeEncoding.Monomorphic:
          AxBuilder = new TypeAxiomBuilderPremisses(gen);
          break;
        default:
          AxBuilder = new TypeAxiomBuilderPremisses(gen);
          AxBuilder.Setup();
          break;
      }
    }

    ProcessStartInfo ComputeProcessStartInfo()
    {
      var path = this.options.ProverPath;
      switch (options.Solver) {
        case SolverKind.Z3:
          if (path == null)
            path = Z3.ExecutablePath();
          return SMTLibProcess.ComputerProcessStartInfo(path, "AUTO_CONFIG=false -smt2 -in");
        case SolverKind.CVC4:
          if (path == null)
            path = CVC4.ExecutablePath();
          return SMTLibProcess.ComputerProcessStartInfo(path, "--lang=smt --no-strict-parsing --no-condense-function-values --incremental");
        default:
          Debug.Assert(false);
          return null;
      }
    }

    void SetupProcess()
    {
      if (Process != null) return;

      var psi = ComputeProcessStartInfo();
      Process = new SMTLibProcess(psi, this.options);
      Process.ErrorHandler += this.HandleProverError;
    }


    void PossiblyRestart()
    {
      if (Process != null && Process.NeedsRestart) {
        Process.Close();
        Process = null;
        SetupProcess();
        Process.Send(common.ToString());
      }
    }

    public override ProverContext Context
    {
      get
      {
        Contract.Ensures(Contract.Result<ProverContext>() != null);

        return ctx;
      }
    }

    internal TypeAxiomBuilder AxBuilder { get; private set; }
    internal readonly UniqueNamer Namer;
    readonly TypeDeclCollector DeclCollector;
    SMTLibProcess Process;
    readonly List<string> proverErrors = new List<string>();
    readonly List<string> proverWarnings = new List<string>();
    readonly StringBuilder common = new StringBuilder();
    TextWriter currentLogFile;
    volatile ErrorHandler currentErrorHandler;

    private void FeedTypeDeclsToProver()
    {
      foreach (string s in DeclCollector.GetNewDeclarations()) {
        Contract.Assert(s != null);
        AddTypeDecl(s);
      }
    }

    private string Sanitize(string msg)
    {
      var idx = msg.IndexOf('\n');
      if (idx > 0)
        msg = msg.Replace("\r", "").Replace("\n", "\r\n");
      return msg;
    }

    private void SendCommon(string s)
    {
      Send(s, true);
    }

    private void SendThisVC(string s)
    {
      Send(s, false);
    }

    private void Send(string s, bool isCommon)
    {
      s = Sanitize(s);

      if (isCommon)
        common.Append(s).Append("\r\n");

      if (Process != null)
        Process.Send(s);
      if (currentLogFile != null)
        currentLogFile.WriteLine(s);
    }

    private void FindDependentTypes(Type type, List<CtorType> dependentTypes)
    {
        MapType mapType = type as MapType;
        if (mapType != null)
        {
            foreach (Type t in mapType.Arguments)
            {
                FindDependentTypes(t, dependentTypes);
            }
            FindDependentTypes(mapType.Result, dependentTypes);
        }
        CtorType ctorType = type as CtorType;
        if (ctorType != null && ctx.KnownDatatypeConstructors.ContainsKey(ctorType))
        {
            dependentTypes.Add(ctorType);
        }
    }

    private void PrepareCommon()
    {
      if (common.Length == 0)
      {
        SendCommon("(set-option :print-success false)");
        SendCommon("(set-info :smt-lib-version 2.0)");
        if (options.ProduceModel())
          SendCommon("(set-option :produce-models true)");
        foreach (var opt in options.SmtOptions)
        {
          SendCommon("(set-option :" + opt.Option + " " + opt.Value + ")");
        }

        if (!string.IsNullOrEmpty(options.Logic))
        {
          SendCommon("(set-logic " + options.Logic + ")");
        }

        SendCommon("; done setting options\n");
        SendCommon(_backgroundPredicates);

        if (options.UseTickleBool)
        {
          SendCommon("(declare-fun tickleBool (Bool) Bool)");
          SendCommon("(assert (and (tickleBool true) (tickleBool false)))");
        }

        if (ctx.KnownDatatypeConstructors.Count > 0)
        {
          GraphUtil.Graph<CtorType> dependencyGraph = new GraphUtil.Graph<CtorType>();
          foreach (CtorType datatype in ctx.KnownDatatypeConstructors.Keys)
          {
            dependencyGraph.AddSource(datatype);
            foreach (Function f in ctx.KnownDatatypeConstructors[datatype])
            {
              List<CtorType> dependentTypes = new List<CtorType>();
              foreach (Variable v in f.InParams)
              {
                FindDependentTypes(v.TypedIdent.Type, dependentTypes);
              }
              foreach (CtorType result in dependentTypes)
              {
                dependencyGraph.AddEdge(datatype, result);
              }
            }
          }
          GraphUtil.StronglyConnectedComponents<CtorType> sccs = new GraphUtil.StronglyConnectedComponents<CtorType>(dependencyGraph.Nodes, dependencyGraph.Predecessors, dependencyGraph.Successors);
          sccs.Compute();
          foreach (GraphUtil.SCC<CtorType> scc in sccs)
          {
            string datatypeString = "";
            foreach (CtorType datatype in scc)
            {
              datatypeString += "(" + SMTLibExprLineariser.TypeToString(datatype) + " ";
              foreach (Function f in ctx.KnownDatatypeConstructors[datatype])
              {
                string quotedConstructorName = Namer.GetQuotedName(f, f.Name);
                if (f.InParams.Count == 0)
                {
                  datatypeString += quotedConstructorName + " ";
                }
                else
                {
                  datatypeString += "(" + quotedConstructorName + " ";
                  foreach (Variable v in f.InParams)
                  {
                    string quotedSelectorName = Namer.GetQuotedName(v, v.Name + "#" + f.Name);
                    datatypeString += "(" + quotedSelectorName + " " + DeclCollector.TypeToStringReg(v.TypedIdent.Type) + ") ";
                  }
                  datatypeString += ") ";
                }
              }
              datatypeString += ") ";
            }
            List<string> decls = DeclCollector.GetNewDeclarations();
            foreach (string decl in decls)
            {
              SendCommon(decl);
            }
            SendCommon("(declare-datatypes () (" + datatypeString + "))");
          }
        }
      }

      if (!AxiomsAreSetup)
      {
        var axioms = ctx.Axioms;
        var nary = axioms as VCExprNAry;
        if (nary != null && nary.Op == VCExpressionGenerator.AndOp)
          foreach (var expr in nary.UniformArguments)
          {
            var str = VCExpr2String(expr, -1);
            if (str != "true")
              AddAxiom(str);
          }
        else
          AddAxiom(VCExpr2String(axioms, -1));
        AxiomsAreSetup = true;
      }
    }

    public override int FlushAxiomsToTheoremProver()
    {
      // we feed the axioms when begincheck is called.
      return 0;
    }

    private void FlushAxioms()
    {
      TypeDecls.Iter(SendCommon);
      TypeDecls.Clear();
      foreach (string s in Axioms) {
        Contract.Assert(s != null);
        if (s != "true")
          SendCommon("(assert " + s + ")");
      }
      Axioms.Clear();
      //FlushPushedAssertions();
    }

    private void CloseLogFile()
    {
      if (currentLogFile != null) {
        currentLogFile.Close();
        currentLogFile = null;
      }
    }

    private void FlushLogFile()
    {
      if (currentLogFile != null) {
        currentLogFile.Flush();
      }
    }

    public override void Close()
    {
      base.Close();
      CloseLogFile();
      if (Process != null)
        Process.Close();
    }

    public override void BeginCheck(string descriptiveName, VCExpr vc, ErrorHandler handler)
    {
      //Contract.Requires(descriptiveName != null);
      //Contract.Requires(vc != null);
      //Contract.Requires(handler != null);
      rpfp = null;

      if (options.SeparateLogFiles) CloseLogFile(); // shouldn't really happen

      if (options.LogFilename != null && currentLogFile == null)
      {
        currentLogFile = OpenOutputFile(descriptiveName);
        currentLogFile.Write(common.ToString());
      }

      PrepareCommon();
      string vcString = "(assert (not\n" + VCExpr2String(vc, 1) + "\n))";
      FlushAxioms();

      PossiblyRestart();

      SendThisVC("(push 1)");
      SendThisVC("(set-info :boogie-vc-id " + SMTLibNamer.QuoteId(descriptiveName) + ")");
      SendThisVC(vcString);
      FlushLogFile();

      if (Process != null) {
        Process.PingPong(); // flush any errors

        if (Process.Inspector != null)
          Process.Inspector.NewProblem(descriptiveName, vc, handler);
      }

      SendThisVC("(check-sat)");
      FlushLogFile();
    }

    public override void Reset(VCExpressionGenerator gen)
    {
      if (options.Solver == SolverKind.Z3)
      {
        this.gen = gen;
        SendThisVC("(reset)");

        if (0 < common.Length)
        {
          var c = common.ToString();
          Process.Send(c);
          if (currentLogFile != null)
          {
            currentLogFile.WriteLine(c);
          }
        }
      }
    }

    public override void FullReset(VCExpressionGenerator gen)
    {
      if (options.Solver == SolverKind.Z3)
      {
        this.gen = gen;
        Namer.Reset();
        common.Clear();
        SetupAxiomBuilder(gen);
        Axioms.Clear();
        TypeDecls.Clear();
        AxiomsAreSetup = false;
        ctx.Reset();
        ctx.KnownDatatypeConstructors.Clear();
        ctx.parent = this;
        DeclCollector.Reset();
        SendThisVC("; doing a full reset...");
      }
    }
	

		
		private string StripCruft(string name){
			if(name.Contains("@@"))
			  return name.Remove(name.LastIndexOf ("@@"));
			return name;
		}
		
		private class BadExprFromProver : Exception
		{
		};
		
		private delegate VCExpr ArgGetter (int pos);
		
		private delegate VCExpr[] ArgsGetter ();
		
		private delegate VCExprVar[] VarsGetter ();
		
		private VCExprOp VCStringToVCOp (string op)
		{
			switch (op) {
			case "+" :
				return VCExpressionGenerator.AddIOp;
			case "-" :
				return VCExpressionGenerator.SubIOp;
			case "*" :
				return VCExpressionGenerator.MulIOp;
			case "div" :
				return VCExpressionGenerator.DivIOp;
			case "=" :
				return VCExpressionGenerator.EqOp;
			case "<=" :
				return VCExpressionGenerator.LeOp;
			case "<" :
				return VCExpressionGenerator.LtOp;
			case ">=" :
				return VCExpressionGenerator.GeOp;
			case ">" :
				return VCExpressionGenerator.GtOp;
			case "and" :
				return VCExpressionGenerator.AndOp;
			case "or" :
				return VCExpressionGenerator.OrOp;
			case "not" :
				return VCExpressionGenerator.NotOp;
			case "ite" :
				return VCExpressionGenerator.IfThenElseOp;
			default:
				return null;
			}
		}
		
		private class MyDeclHandler : TypeDeclCollector.DeclHandler {
			public Dictionary<string,VCExprVar> var_map = new Dictionary<string, VCExprVar>();
			public Dictionary<string,Function> func_map = new Dictionary<string, Function>();
			public override void VarDecl(VCExprVar v){
				var_map[v.Name] = v;
			}
			public override void FuncDecl(Function f){
				func_map[f.Name] = f;
			}
			public MyDeclHandler() {
			}
		}
		
		private MyDeclHandler declHandler = null;

		private VCExprVar SExprToVar (SExpr e)
		{
			if(e.Arguments.Count() != 1){
				HandleProverError ("Prover error: bad quantifier syntax");
				throw new BadExprFromProver ();
			}
			string vname = StripCruft(e.Name);
			SExpr vtype = e[0];
			switch(vtype.Name){
			case "Int":
				return gen.Variable(vname,Type.Int);
			case "Bool":
				return gen.Variable (vname,Type.Bool);
			case "Array":{
                // TODO: handle more general array types
                var idxType = Type.Int; // well, could be something else
                var valueType = 
                    (vtype.Arguments[1].Name == "Int") ? Type.Int : Type.Bool;
                var types = new List<Type>();
                types.Add(idxType);
				return gen.Variable (vname, new MapType(Token.NoToken,new List<TypeVariable>(),types,valueType));
            }
			default: {
				HandleProverError ("Prover error: bad type: " + vtype.Name);
				throw new BadExprFromProver ();
				}
			}
		}

        private VCExpr MakeBinary(VCExprOp op, VCExpr [] args)
        {
            if (args.Count() == 0)
            {
                // with zero args we need the identity of the op
                if (op == VCExpressionGenerator.AndOp)
                    return VCExpressionGenerator.True;
                if (op == VCExpressionGenerator.OrOp)
                    return VCExpressionGenerator.False;
                if (op == VCExpressionGenerator.AddIOp)
                {
                    Microsoft.Basetypes.BigNum x = Microsoft.Basetypes.BigNum.ZERO;
                    return gen.Integer(x);
                }
                HandleProverError("Prover error: bad expression ");
                throw new BadExprFromProver();
            }
            var temp = args[0];
            for (int i = 1; i < args.Count(); i++)
                temp = gen.Function(op, temp, args[i]);
            return temp;
        }
		
		private VCExpr SExprToVCExpr (SExpr e, Dictionary<string,VCExpr> bound)
		{
			if (e.Arguments.Count() == 0) {
				var name = StripCruft(e.Name);
				if (name [0] >= '0' && name [0] <= '9') {
					Microsoft.Basetypes.BigNum x = Microsoft.Basetypes.BigNum.FromString(name);
					return gen.Integer (x);
				}
				if (bound.ContainsKey (name)) {
					return bound [name];
				}
				if(name == "true")
					return VCExpressionGenerator.True;
				if(name == "false")
					return VCExpressionGenerator.False;
				if(declHandler.var_map.ContainsKey(name))
					return declHandler.var_map[name];
				HandleProverError ("Prover error: unknown symbol:" + name);
				throw new BadExprFromProver ();
			}
			ArgGetter g = i => SExprToVCExpr (e [i], bound);
			ArgsGetter ga = () => e.Arguments.Select (x => SExprToVCExpr (x, bound)).ToArray ();
			VarsGetter gb = () => e [0].Arguments.Select (x => SExprToVar (x)).ToArray ();
			switch (e.Name) {
			case "select" :
				return gen.Select (ga ());
			case "store" :
				return gen.Store (ga ());
            case "forall":
            case "exists":
                {
                    var binds = e.Arguments[0];
                    var vcbinds = new List<VCExprVar>();
                    for (int i = 0; i < binds.Arguments.Count(); i++)
                    {
                        var bind = binds.Arguments[i];
                        var symb = bind.Name;
                        var vcv = SExprToVar(bind);
                        vcbinds.Add(vcv);
                        bound[symb] = vcv;
                    }
                    var body = g(1);
                    if (e.Name == "forall")
                        body = gen.Forall(vcbinds, new List<VCTrigger>(), body);
                    else
                        body = gen.Exists(vcbinds, new List<VCTrigger>(), body);
                    for (int i = 0; i < binds.Arguments.Count(); i++)
                    {
                        var bind = binds.Arguments[i];
                        var symb = bind.Name;
                        bound.Remove(symb);
                    }
                    return body;
                }
			case "-" : // have to deal with unary case
			{
				if(e.ArgCount == 1){
					var args = new VCExpr[2];
					args[0] = gen.Integer (Microsoft.Basetypes.BigNum.ZERO);
					args[1] = g(0);
					return gen.Function(VCStringToVCOp("-"),args);
				}
				return gen.Function(VCStringToVCOp("-"),ga());
			}
            case "!" : // this is commentary
                return g(0);
            case "let" : {
				// we expand lets exponentially since there is no let binding in Boogie surface syntax
				bool expand_lets = true;
				var binds = e.Arguments[0];
				var vcbinds = new List<VCExprLetBinding>();
				for(int i = 0; i < binds.Arguments.Count(); i++){
					var bind = binds.Arguments[i];
					var symb = bind.Name;
					var def = bind.Arguments[0];
                    var vce = SExprToVCExpr(def, bound);
					var vcv = gen.Variable(symb,vce.Type);
					var vcb = gen.LetBinding(vcv,vce);
					vcbinds.Add (vcb);
					bound[symb] = expand_lets ? vce : vcv;
				}
				var body = g(1); 
				if(!expand_lets)
					body = gen.Let(vcbinds,body);
				for(int i = 0; i < binds.Arguments.Count(); i++){
					var bind = binds.Arguments[i];
					var symb = bind.Name;
					bound.Remove (symb);
				}
                return body;
			}
				
			default: {
					var op = VCStringToVCOp (e.Name);
					if (op == null) {
						var name = StripCruft(e.Name);
					    if(declHandler.func_map.ContainsKey(name)){
							Function f = declHandler.func_map[name];	
							return gen.Function (f, ga());
					    }
						HandleProverError ("Prover error: unknown operator:" + e.Name);
						throw new BadExprFromProver ();
					}
                    if(op.Arity == 2)
					    return MakeBinary (op, ga ());
                    return gen.Function(op, ga());
				}
			}
		}
		
		private void SExprToSoln (SExpr resp,
                             Dictionary<int,Dictionary<string,string>> varSubst)
		{
			Dictionary<string, RPFP.Node> pmap = new Dictionary<string,RPFP.Node> ();

			foreach (var node in rpfp.nodes)
				pmap.Add ((node.Name as VCExprBoogieFunctionOp).Func.Name, node);
			
			var lines = resp.Arguments;

			// get all the predicate definitions
			for (int i = 0; i < lines.Length; i++) {
				var line = lines [i];
                string pname;
                RPFP.Transformer annot;
                GetDefun(line, out pname, out annot);
				
				if(pmap.ContainsKey(pname)){
					var node = pmap[pname];
					node.Annotation = annot;
				}
				else if(pname[0] != '@'){ // if not an internal symbol
					HandleProverError ("Prover error: got unknown predicate:" + pname);
					throw new BadExprFromProver ();
				}
			}
			
		}

        private void GetDefun(SExpr line, out string pname, out RPFP.Transformer annot)
        {
            if (line.Name != "define-fun")
            {
                HandleProverError("Prover error: expected define-fun but got:" + line.Name);
                throw new BadExprFromProver();
            }
            if (line.ArgCount != 4)
            {
                HandleProverError("Prover error: define-fun has wrong number of arguments");
                throw new BadExprFromProver();
            }
            pname = StripCruft(line.Arguments[0].Name);
            var pvars = line.Arguments[1];
            var pbody = line.Arguments[3]; // range has to be Bool
            var binding = new Dictionary<string, VCExpr>();
            var pvs = new List<VCExpr>();
            foreach (var b in pvars.Arguments)
            {
                var e = SExprToVar(b);
                pvs.Add(e);
                binding.Add(StripCruft(b.Name), e);
            }
            VCExpr bexpr = SExprToVCExpr(pbody, binding);

            annot = rpfp.CreateRelation(pvs.ToArray(), bexpr);
        }
		
    private RPFP.Node SExprToCex(SExpr resp, ErrorHandler handler, 
                                 Dictionary<int,Dictionary<string,string>> varSubst)
    {
        Dictionary<string, RPFP.Node> nmap = new Dictionary<string,RPFP.Node>();
        Dictionary<string, RPFP.Node> pmap = new Dictionary<string,RPFP.Node>();

        foreach(var node in rpfp.nodes)
            pmap.Add((node.Name as VCExprBoogieFunctionOp).Func.Name,node);

        RPFP.Node topnode = null;
        var lines = resp.Arguments;

        // last line of derivation is from query, skip it
        for (int i = 0; i < lines.Length-1; i++)
        {
            var line = lines[i];
            if (line.ArgCount != 6)
            {
                HandleProverError("bad derivation line from prover: " + line.ToString());
                return null;
            }
            var name = line[0];
            var conseq = line[1];
            var rule = line[2];
            var subst = line[3];
            var labs = line[4];
            var refs = line[5];
            var predName = conseq.Name;
            {
                string spacer = "@@"; // Hack! UniqueNamer is adding these and I can't stop it!
                int pos = predName.LastIndexOf(spacer);
                if (pos >= 0)
                    predName = predName.Substring(0, pos);
            }
            RPFP.Node node = null;
            if (!pmap.TryGetValue(predName, out node))
            {
                HandleProverError("unknown predicate from prover: " + predName.ToString());
                return null;
            }
            RPFP.Node cexnode = rpfp.CloneNode(node);
            cexnode.map = node;
            nmap.Add(name.Name, cexnode);
            List<RPFP.Node> Chs = new List<RPFP.Node>();

            if (refs.Name != "ref")
            {
                HandleProverError("bad references from prover: " + refs.ToString());
                return null;
            }
            foreach (var c in refs.Arguments)
            {
                if (c.Name == "true")
                    Chs.Add(null);
                else
                {
                    RPFP.Node ch = null;
                    if (!nmap.TryGetValue(c.Name, out ch))
                    {
                        HandleProverError("unknown reference from prover: " + c.ToString());
                        return null;
                    }
                    Chs.Add(ch);
                }
            }

            if (!rule.Name.StartsWith("rule!"))
            {
                HandleProverError("bad rule name from prover: " + refs.ToString());
                return null;
            }
            int ruleNum = Convert.ToInt32(rule.Name.Substring(5)) - 1;
            if (ruleNum < 0 || ruleNum > rpfp.edges.Count)
            {
                HandleProverError("bad rule name from prover: " + refs.ToString());
                return null;
            }
            RPFP.Edge orig_edge = rpfp.edges[ruleNum];
            RPFP.Edge e = rpfp.CreateEdge(cexnode, orig_edge.F, Chs.ToArray());
            e.map = orig_edge;
            topnode = cexnode;

            if (labs.Name != "labels")
            {
                HandleProverError("bad labels from prover: " + labs.ToString());
                return null;
            }
            e.labels = new HashSet<string>();
            foreach (var l in labs.Arguments)
                e.labels.Add(l.Name);

            if (subst.Name != "subst")
            {
                HandleProverError("bad subst from prover: " + subst.ToString());
                return null;
            }
            Dictionary<string, string> dict = new Dictionary<string, string>();
            varSubst[e.number] = dict;
            foreach (var s in subst.Arguments)
            {
                if (s.Name != "=" || s.Arguments.Length != 2)
                {
                    HandleProverError("bad equation from prover: " + s.ToString());
                    return null;
                }
                string uniqueName = s.Arguments[0].Name;
                string spacer = "@@"; // Hack! UniqueNamer is adding these and I can't stop it!
                int pos = uniqueName.LastIndexOf(spacer);
                if (pos >= 0)
                    uniqueName = uniqueName.Substring(0, pos);
                dict.Add(uniqueName, s.Arguments[1].ToString());
            }

        }
        if (topnode == null)
        {
            HandleProverError("empty derivation from prover: " + resp.ToString());
        }
        return topnode;
    }

    private Model SExprToModel(SExpr resp, ErrorHandler handler)
    {
        // Concatenate all the arguments
        string modelString = resp[0].Name;
        // modelString = modelString.Substring(7, modelString.Length - 8); // remove "(model " and final ")"
        var models = Model.ParseModels(new StringReader("Error model: \n" + modelString), "");
        if (models == null || models.Count == 0)
        {
            HandleProverError("no model from prover: " + resp.ToString());
        }
        return models[0];
    }

    private string QuantifiedVCExpr2String(VCExpr x)
    {
        return VCExpr2String(x, 1); 
#if false
        if (!(x is VCExprQuantifier))
            return VCExpr2String(x, 1);
        VCExprQuantifier node = (x as VCExprQuantifier);
        if(node.BoundVars.Count == 0)
            return VCExpr2String(x, 1);

        StringWriter wr = new StringWriter();

        string kind = node.Quan == Quantifier.ALL ? "forall" : "exists"; 
        wr.Write("({0} (", kind);

        for (int i = 0; i < node.BoundVars.Count; i++)
        {
            VCExprVar var = node.BoundVars[i];
            Contract.Assert(var != null);
            string printedName = Namer.GetQuotedName(var, var.Name);
            Contract.Assert(printedName != null);
            wr.Write("({0} {1}) ", printedName, SMTLibExprLineariser.TypeToString(var.Type));
        }

        wr.Write(") ");
        wr.Write(VCExpr2String(node.Body, 1));
        wr.Write(")");
        string res = wr.ToString();
        return res;
#endif
    }
		
    public override Outcome CheckRPFP(string descriptiveName, RPFP _rpfp, ErrorHandler handler, 
                                      out RPFP.Node cex,
                                      Dictionary<int,Dictionary<string,string>> varSubst)
    {
        //Contract.Requires(descriptiveName != null);
        //Contract.Requires(vc != null);
        //Contract.Requires(handler != null);
        rpfp = _rpfp;
        cex = null;
			
			
        if (options.SeparateLogFiles) CloseLogFile(); // shouldn't really happen

        if (options.LogFilename != null && currentLogFile == null)
        {
            currentLogFile = OpenOutputFile(descriptiveName);
            currentLogFile.Write(common.ToString());
        }

        PrepareCommon(); 
        
        Push();
        SendThisVC("(fixedpoint-push)");
        foreach (var node in rpfp.nodes)
        {
            DeclCollector.RegisterRelation((node.Name as VCExprBoogieFunctionOp).Func);
        }

        LineariserOptions.Default.LabelsBelowQuantifiers = true;
        List<string> ruleStrings = new List<string>();
        foreach (var edge in rpfp.edges)
        {
            string ruleString = "(rule " + QuantifiedVCExpr2String(rpfp.GetRule(edge)) + "\n)";
            ruleStrings.Add(ruleString);
        }
        string queryString = "(query " + QuantifiedVCExpr2String(rpfp.GetQuery()) + "\n   :engine duality\n  :print-certificate true\n";
       
#if true
        if (CommandLineOptions.Clo.StratifiedInlining != 0)
            queryString += "    :stratified-inlining true\n";
        if (CommandLineOptions.Clo.RecursionBound > 0)
            queryString += "    :recursion-bound " + Convert.ToString(CommandLineOptions.Clo.RecursionBound) + "\n";
#endif
        queryString += ")";
        LineariserOptions.Default.LabelsBelowQuantifiers = false;
        FlushAxioms();

        PossiblyRestart();

        SendThisVC("(set-info :boogie-vc-id " + SMTLibNamer.QuoteId(descriptiveName) + ")");
        foreach(var rs in ruleStrings)
            SendThisVC(rs);
        FlushLogFile();

        if (Process != null)
        {
            Process.PingPong(); // flush any errors

#if false
            // TODO: this is not going to work
            if (Process.Inspector != null)
                Process.Inspector.NewProblem(descriptiveName, vc, handler);
#endif
        }

        SendThisVC(queryString);
        FlushLogFile();

        var result = Outcome.Undetermined;

        if (Process != null)
        {
            
            var resp = Process.GetProverResponse();

            if (proverErrors.Count > 0)
            {
                result = Outcome.Undetermined;
                foreach (var err in proverErrors)
                {
                    if (err.Contains("canceled"))
                    {
                        result = Outcome.TimeOut;
                    }
                }
            }
            else if(resp == null)
                HandleProverError("Prover did not respond");
            else switch (resp.Name)
            {
                case "unsat":
                    result = Outcome.Valid;
                    break;
                case "sat":
                    result = Outcome.Invalid;
                    break;
                case "unknown":
                    result = Outcome.Invalid;
                    break;
                case "error":
                    if (resp.ArgCount > 0 && resp.Arguments[0].Name.Contains("canceled"))
                    {
                        result = Outcome.TimeOut;
                    }
                    else
                    {
                        HandleProverError("Prover error: " + resp.Arguments[0]);
                        result = Outcome.Undetermined;
                    }
                    break;
                default:
                    HandleProverError("Unexpected prover response: " + resp.ToString());
                    break;
            }
            
            switch (result)
            {
                case Outcome.Invalid:
                    {
                        resp = Process.GetProverResponse();
                        if (resp.Name == "derivation")
                        {
                            cex = SExprToCex(resp, handler,varSubst);
                        }
                        else
                            HandleProverError("Unexpected prover response: " + resp.ToString());
                        resp = Process.GetProverResponse();
                        if (resp.Name == "model")
                        {
                            var model = SExprToModel(resp, handler);
                            cex.owner.SetBackgroundModel(model);
                        }
                        else
                            HandleProverError("Unexpected prover response: " + resp.ToString());
                        break;
                    }
				case Outcome.Valid:
				    {
						resp = Process.GetProverResponse();
                        if (resp.Name == "fixedpoint")
                        {
                            // only get the response if we need it
                            if(CommandLineOptions.Clo.PrintFixedPoint != null)
                               SExprToSoln(resp, varSubst);
                        }
						else
							HandleProverError("Unexpected prover response: " + resp.ToString());
					    break;
				    }
                default:
                    break;
            }

#if false
            while (true)
            {
                resp = Process.GetProverResponse();
                if (resp == null || Process.IsPong(resp))
                    break;
                HandleProverError("Unexpected prover response: " + resp.ToString());
            }
#endif
        }
        SendThisVC("(fixedpoint-pop)");
        Pop();
        AxiomsAreSetup = false;

        if (CommandLineOptions.Clo.PrintConjectures != null)
        {
            ReadConjectures(CommandLineOptions.Clo.PrintConjectures);
        }

        return result;
    }

    class MyFileParser : SExpr.Parser
    {
        SMTLibProcessTheoremProver parent;

        public MyFileParser(System.IO.StreamReader _sr, SMTLibProcessTheoremProver _parent)
            : base(_sr)
        {
            parent = _parent;
        }
        public override void ParseError(string msg)
        {
            parent.HandleProverError("Error in conjecture file from prover: " + msg);
        }
    }

    void ReadConjectures(string filename)
    {
        try
        {
            System.IO.StreamReader sr = new StreamReader(filename + ".tmp");
            SExpr.Parser p = new MyFileParser(sr, this);
            var sexps = p.ParseSExprs(false);
            Dictionary<string, RPFP.Node> pmap = GetNodeMap();
            foreach (var e in sexps)
            {
                string pname;
                RPFP.Transformer annot;
                GetDefun(e, out pname, out annot);

                if (pmap.ContainsKey(pname))
                {
                    var c = new RPFP.Conjecture();
                    c.node = pmap[pname];
                    c.bound = annot;
                    rpfp.conjectures.Add(c);
                }
                else if (pname[0] != '@')
                { // if not an internal symbol
                    HandleProverError("Prover error: got unknown predicate:" + pname);
                    throw new BadExprFromProver();
                }
            }
            sr.Close();
        }
        catch (Exception)
        {
            HandleProverError("No conjecture file from prover");
            throw new BadExprFromProver();
        }
    }

    private Dictionary<string, RPFP.Node> GetNodeMap()
    {
        Dictionary<string, RPFP.Node> pmap = new Dictionary<string, RPFP.Node>();
        foreach (var node in rpfp.nodes)
            pmap.Add((node.Name as VCExprBoogieFunctionOp).Func.Name, node);
        return pmap;
    }

    private static HashSet<string> usedLogNames = new HashSet<string>();

    private TextWriter OpenOutputFile(string descriptiveName)
    {
      Contract.Requires(descriptiveName != null);
      Contract.Ensures(Contract.Result<TextWriter>() != null);

      string filename = options.LogFilename;
      filename = Helpers.SubstituteAtPROC(descriptiveName, cce.NonNull(filename));
      var curFilename = filename;

      lock (usedLogNames) {
        int n = 1;
        while (usedLogNames.Contains(curFilename)) {
          curFilename = filename + "." + n++;
        }
        usedLogNames.Add(curFilename);
      }

      return new StreamWriter(curFilename, false);
    }

    private void FlushProverWarnings()
    {
      var handler = currentErrorHandler;
      if (handler != null) {
        lock (proverWarnings) {
          proverWarnings.Iter(handler.OnProverWarning);
          proverWarnings.Clear();
        }
      }
    }

    private void HandleProverError(string s)
    {
      s = s.Replace("\r", "");
      lock (proverWarnings) {
        while (s.StartsWith("WARNING: ")) {
          var idx = s.IndexOf('\n');
          var warn = s;
          if (idx > 0) {
            warn = s.Substring(0, idx);
            s = s.Substring(idx + 1);
          } else {
            s = "";
          }
          warn = warn.Substring(9);
          proverWarnings.Add(warn);
        }
      }

      FlushProverWarnings();

      if (s == "") return;

      lock (proverErrors) {
        proverErrors.Add(s);
        Console.WriteLine("Prover error: " + s);
      }
    }

    [NoDefaultContract]
    public override Outcome CheckOutcome(ErrorHandler handler, int taskID = -1)
    {
      Contract.EnsuresOnThrow<UnexpectedProverOutputException>(true);

      var result = CheckOutcomeCore(handler, taskID: taskID);
      SendThisVC("(pop 1)");
      FlushLogFile();

      return result;
    }

    [NoDefaultContract]
    public override Outcome CheckOutcomeCore(ErrorHandler handler, int taskID = -1)
    {  
      Contract.EnsuresOnThrow<UnexpectedProverOutputException>(true);
      
      var result = Outcome.Undetermined;

      if (Process == null)
        return result;

      try {
        currentErrorHandler = handler;
        FlushProverWarnings();

        int errorsLeft;
        if (CommandLineOptions.Clo.ConcurrentHoudini) {
          Contract.Assert(taskID >= 0);
          errorsLeft = CommandLineOptions.Clo.Cho[taskID].ProverCCLimit;
        } else {
          errorsLeft = CommandLineOptions.Clo.ProverCCLimit;
        }

        if (errorsLeft < 1)
          errorsLeft = 1;

        var globalResult = Outcome.Undetermined;

        while (true) {
          errorsLeft--;
          string[] labels = null;

          result = GetResponse();
          if (globalResult == Outcome.Undetermined)
            globalResult = result;

          if (result == Outcome.Invalid || result == Outcome.TimeOut || result == Outcome.OutOfMemory) {
            IList<string> xlabels;
            if (CommandLineOptions.Clo.UseLabels) {
              labels = GetLabelsInfo();
              if (labels == null)
              {
                xlabels = new string[] { };
              }
              else
              {
                xlabels = labels.Select(a => a.Replace("@", "").Replace("+", "")).ToList();
              }
            }
            else {
              labels = CalculatePath(handler.StartingProcId());
              xlabels = labels;
            }
              Model model = (result == Outcome.TimeOut || result == Outcome.OutOfMemory) ? null :
                  GetErrorModel();
            handler.OnModel(xlabels, model, result);
          }

          if (labels == null || !labels.Any() || errorsLeft == 0) break;

          if (CommandLineOptions.Clo.UseLabels) {
            var negLabels = labels.Where(l => l.StartsWith("@")).ToArray();
            var posLabels = labels.Where(l => !l.StartsWith("@"));
            Func<string, string> lbl = (s) => SMTLibNamer.QuoteId(SMTLibNamer.LabelVar(s));
            if (!options.MultiTraces)
              posLabels = Enumerable.Empty<string>();
            var conjuncts = posLabels.Select(s => "(not " + lbl(s) + ")").Concat(negLabels.Select(lbl)).ToArray();
            string expr = conjuncts.Length == 1 ? conjuncts[0] : ("(or " + conjuncts.Concat(" ") + ")"); ;
            if (!conjuncts.Any())
            {
              expr = "false";
            }
            SendThisVC("(assert " + expr + ")");
            SendThisVC("(check-sat)");
          }
          else {
            string source = labels[labels.Length - 2];
            string target = labels[labels.Length - 1];
            SendThisVC("(assert (not (= (ControlFlow 0 " + source + ") (- " + target + "))))");
            SendThisVC("(check-sat)");
          }
        }

        FlushLogFile();

        if (CommandLineOptions.Clo.RestartProverPerVC && Process != null)
          Process.NeedsRestart = true;

        return globalResult;

      } finally {
        currentErrorHandler = null;
      }
    }

    public override string[] CalculatePath(int controlFlowConstant) {
      SendThisVC("(get-value ((ControlFlow " + controlFlowConstant + " 0)))");
      var path = new List<string>();
      while (true) {
        var resp = Process.GetProverResponse();
        if (resp == null) break;
        if (!(resp.Name == "" && resp.ArgCount == 1)) break;
        resp = resp.Arguments[0];
        if (!(resp.Name == "" && resp.ArgCount == 2)) break;
        resp = resp.Arguments[1];
        var v = resp.Name;
        if (v == "-" && resp.ArgCount == 1) {
          v = resp.Arguments[0].Name;
          path.Add(v);
          break;
        }
        else if (resp.ArgCount != 0)
          break;
        path.Add(v);
        SendThisVC("(get-value ((ControlFlow " + controlFlowConstant + " " + v + ")))");
      }
      return path.ToArray();
    }

    private Model GetErrorModel() {
      if (!options.ExpectingModel())
        return null;
      SendThisVC("(get-model)");
      Process.Ping();
      Model theModel = null;
      while (true) {
        var resp = Process.GetProverResponse();
        if (resp == null || Process.IsPong(resp))
          break;
        if (theModel != null)
          HandleProverError("Expecting only one model but got many");
        
		
        string modelStr = null;
        if (resp.Name == "model" && resp.ArgCount >= 1) {
          modelStr = resp.Arguments[0] + "\n";
          for (int i = 1; i < resp.ArgCount; i++) {
            modelStr += resp.Arguments[i] + "\n";
          }
        }
        else if (resp.ArgCount == 0 && resp.Name.Contains("->")) {
          modelStr = resp.Name;
        }
        else {
          HandleProverError("Unexpected prover response getting model: " + resp.ToString());
        }
        
        List<Model> models = null;
        try {
          switch (options.Solver) {
            case SolverKind.Z3:
              if (CommandLineOptions.Clo.UseSmtOutputFormat) {
                models = Model.ParseModels(new StringReader("Error model: \n" + modelStr), "SMTLIB2");
              } else {
                models = Model.ParseModels(new StringReader("Error model: \n" + modelStr), "");
              }
              break;
            case SolverKind.CVC4:
              models = Model.ParseModels(new StringReader("Error model: \n" + modelStr), "SMTLIB2");
              break;
            default:
              Debug.Assert(false);
              return null;
          }
        }
        catch (ArgumentException exn) {
          HandleProverError("Model parsing error: " + exn.Message);
        }
        if (models == null)
          HandleProverError("Could not parse any models");
        else if (models.Count == 0)
          HandleProverError("Could not parse any models");
        else if (models.Count > 1)
          HandleProverError("Expecting only one model but got many");
        else
          theModel = models[0];
      }
      return theModel;
    }

    private string[] GetLabelsInfo()
    {
      SendThisVC("(labels)");
      Process.Ping();

      string[] res = null;
      while (true) {
        var resp = Process.GetProverResponse();
        if (resp == null || Process.IsPong(resp))
          break;
        if (res != null)
          HandleProverError("Expecting only one sequence of labels but got many");
        if (resp.Name == "labels" && resp.ArgCount >= 1) {
          res = resp.Arguments.Select(a => a.Name.Replace("|", "")).ToArray();
        }
        else {
          HandleProverError("Unexpected prover response getting labels: " + resp.ToString());
        }
      }
      return res;
    }

    private Outcome GetResponse()
    {
      var result = Outcome.Undetermined;
      var wasUnknown = false;

      Process.Ping();

      while (true) {
        var resp = Process.GetProverResponse();
        if (resp == null || Process.IsPong(resp))
          break;

        switch (resp.Name) {
          case "unsat":
            result = Outcome.Valid;
            break;
          case "sat":
            result = Outcome.Invalid;
            break;
          case "unknown":
            result = Outcome.Invalid;
            wasUnknown = true;
            break;
          default:
            HandleProverError("Unexpected prover response: " + resp.ToString());
            break;
        }
      }

      
      if (wasUnknown) {
        SendThisVC("(get-info :reason-unknown)");
        Process.Ping();
        while (true) {
          var resp = Process.GetProverResponse();
          if (resp == null || Process.IsPong(resp))
              break;

          if (resp.ArgCount == 1 && resp.Name == ":reason-unknown") {
            switch (resp[0].Name) {
              case "memout":
                currentErrorHandler.OnResourceExceeded("memory");
                result = Outcome.OutOfMemory;
                Process.NeedsRestart = true;
                break;
                case "timeout": case "canceled":
                currentErrorHandler.OnResourceExceeded("timeout");
                result = Outcome.TimeOut;
                break;
              default:
                break;
            }
          } else {
            HandleProverError("Unexpected prover response (getting info about 'unknown' response): " + resp.ToString());
          }
        }

      }

      return result;
    }

    protected string VCExpr2String(VCExpr expr, int polarity)
    {
      Contract.Requires(expr != null);
      Contract.Ensures(Contract.Result<string>() != null);

      lock (gen)
      {
        DateTime start = DateTime.UtcNow;
        //if (CommandLineOptions.Clo.Trace)
        //  Console.Write("Linearising ... ");

        // handle the types in the VCExpr
        TypeEraser eraser;
        switch (CommandLineOptions.Clo.TypeEncodingMethod)
        {
          case CommandLineOptions.TypeEncoding.Arguments:
            eraser = new TypeEraserArguments((TypeAxiomBuilderArguments)AxBuilder, gen);
            break;
          case CommandLineOptions.TypeEncoding.Monomorphic:
            eraser = null;
            break;
          default:
            eraser = new TypeEraserPremisses((TypeAxiomBuilderPremisses)AxBuilder, gen);
            break;
        }
        VCExpr exprWithoutTypes = eraser == null ? expr : eraser.Erase(expr, polarity);
        Contract.Assert(exprWithoutTypes != null);

        LetBindingSorter letSorter = new LetBindingSorter(gen);
        Contract.Assert(letSorter != null);
        VCExpr sortedExpr = letSorter.Mutate(exprWithoutTypes, true);
        Contract.Assert(sortedExpr != null);
        VCExpr sortedAxioms = letSorter.Mutate(AxBuilder.GetNewAxioms(), true);
        Contract.Assert(sortedAxioms != null);

        DeclCollector.Collect(sortedAxioms);
        DeclCollector.Collect(sortedExpr);
        FeedTypeDeclsToProver();



        AddAxiom(SMTLibExprLineariser.ToString(sortedAxioms, Namer, options));
        string res = SMTLibExprLineariser.ToString(sortedExpr, Namer, options);
        Contract.Assert(res != null);

        if (CommandLineOptions.Clo.Trace)
        {
          DateTime end = DateTime.UtcNow;
          TimeSpan elapsed = end - start;
          if (elapsed.TotalSeconds > 0.5)
            Console.WriteLine("Linearising   [{0} s]", elapsed.TotalSeconds);
        }
        return res;
      }
    }

    // the list of all known axioms, where have to be included in each
    // verification condition
    private readonly List<string/*!>!*/> Axioms = new List<string/*!*/>();
    private bool AxiomsAreSetup = false;




    // similarly, a list of function/predicate declarations
    private readonly List<string/*!>!*/> TypeDecls = new List<string/*!*/>();

    protected void AddAxiom(string axiom)
    {
      Contract.Requires(axiom != null);
      Axioms.Add(axiom);
      //      if (thmProver != null) {
      //        LogActivity(":assume " + axiom);
      //        thmProver.AddAxioms(axiom);
      //      }
    }

    protected void AddTypeDecl(string decl)
    {
      Contract.Requires(decl != null);
      TypeDecls.Add(decl);
      //     if (thmProver != null) {
      //       LogActivity(decl);
      //       thmProver.Feed(decl, 0);
      //     }
    }

    ////////////////////////////////////////////////////////////////////////////

    private static string _backgroundPredicates;

    static void InitializeGlobalInformation()
    {
      Contract.Ensures(_backgroundPredicates != null);
      //throws ProverException, System.IO.FileNotFoundException;
      if (_backgroundPredicates == null) {
        if (CommandLineOptions.Clo.TypeEncodingMethod == CommandLineOptions.TypeEncoding.Monomorphic)
        {
            _backgroundPredicates = "";
        }
        else
        {
            _backgroundPredicates = @"
(set-info :category ""industrial"")
(declare-sort |T@U| 0)
(declare-sort |T@T| 0)
(declare-fun real_pow (Real Real) Real)
(declare-fun UOrdering2 (|T@U| |T@U|) Bool)
(declare-fun UOrdering3 (|T@T| |T@U| |T@U|) Bool)";
        }
      }
    }

    public override VCExpressionGenerator VCExprGen
    {
      get { return this.gen; }
    }

    //// Push/pop interface

    //List<string> pushedAssertions = new List<string>();
    //int numRealPushes;
    public override string VCExpressionToString(VCExpr vc)
    {
      return VCExpr2String(vc, 1);
    }

    public override void PushVCExpression(VCExpr vc)
    {
        throw new NotImplementedException();

    }

    public override void Pop()
    {
      SendThisVC("(pop 1)");
      DeclCollector.Pop();
    }

    public override int NumAxiomsPushed()
    {
        throw new NotImplementedException();
        //return numRealPushes + pushedAssertions.Count;
    }

    private void FlushPushedAssertions()
    {
        throw new NotImplementedException();
    }

    public override void Assert(VCExpr vc, bool polarity)
    {
        string a = "";
        if (polarity)
        {
            a = "(assert " + VCExpr2String(vc, 1) + ")";
        }
        else
        {
            a = "(assert (not\n" + VCExpr2String(vc, 1) + "\n))";
        }
        AssertAxioms();
        SendThisVC(a);
    }

    public override void DefineMacro(Macro f, VCExpr vc) {
      DeclCollector.AddFunction(f);
      string printedName = Namer.GetQuotedName(f, f.Name);
      var argTypes = f.InParams.Cast<Variable>().MapConcat(p => DeclCollector.TypeToStringReg(p.TypedIdent.Type), " ");
      string decl = "(define-fun " + printedName + " (" + argTypes + ") " + DeclCollector.TypeToStringReg(f.OutParams[0].TypedIdent.Type) + " " + VCExpr2String(vc, 1) + ")";
      AssertAxioms();
      SendThisVC(decl); 
    }

    public override void AssertAxioms()
    {
        FlushAxioms();
    }

    public override void Check()
    {
        PrepareCommon();
        SendThisVC("(check-sat)");
        FlushLogFile();
    }
	
    public override void SetTimeOut(int ms)
    {
	    if (options.Solver == SolverKind.Z3) {
            var name = Z3.SetTimeoutOption();
            var value = ms.ToString();
            options.TimeLimit = ms;
            options.SmtOptions.RemoveAll(ov => ov.Option == name);
            options.AddSmtOption(name, value);
            SendThisVC(string.Format("(set-option :{0} {1})", name, value));
	    }
    }

    public override object Evaluate(VCExpr expr)
    {
        string vcString = VCExpr2String(expr, 1);
        SendThisVC("(get-value (" + vcString + "))");
        var resp = Process.GetProverResponse();
        if (resp == null) throw new VCExprEvaluationException();
        if (!(resp.Name == "" && resp.ArgCount == 1)) throw new VCExprEvaluationException();
        resp = resp.Arguments[0];
        if (resp.Name == "")
        {
            // evaluating an expression
            if (resp.ArgCount == 2)
                resp = resp.Arguments[1];
            else
                throw new VCExprEvaluationException();
        }
        else
        {
            // evaluating a variable
            if (resp.ArgCount == 1)
                resp = resp.Arguments[0];
            else
                throw new VCExprEvaluationException();
        }
        if (resp.Name == "-" && resp.ArgCount == 1)
            return Microsoft.Basetypes.BigNum.FromString("-" + resp.Arguments[0].Name);
        if (resp.ArgCount != 0)
            throw new VCExprEvaluationException();
        if (expr.Type.Equals(Boogie.Type.Bool))
            return bool.Parse(resp.Name);
        else if (expr.Type.Equals(Boogie.Type.Int))
            return Microsoft.Basetypes.BigNum.FromString(resp.Name); 
        else
            return resp.Name;
    }

    /// <summary>
    /// Extra state for ApiChecker (used by stratifiedInlining)
    /// </summary>
    static int nameCounter = 0;

    public override Outcome CheckAssumptions(List<VCExpr> assumptions, out List<int> unsatCore, ErrorHandler handler)
    {
        unsatCore = new List<int>();

        Push();
        // Name the assumptions
        var nameToAssumption = new Dictionary<string, int>();
        int i = 0;
        foreach (var vc in assumptions)
        {
            var name = "a" + nameCounter.ToString();
            nameCounter++;
            nameToAssumption.Add(name, i);

            string vcString = VCExpr2String(vc, 1);
            AssertAxioms();
            SendThisVC(string.Format("(assert (! {0} :named {1}))", vcString, name));
            i++;
        }
        Check();
        
        var outcome = CheckOutcomeCore(handler);

        if (outcome != Outcome.Valid) {
          Pop();
          return outcome;
        }

        Contract.Assert(usingUnsatCore, "SMTLib prover not setup for computing unsat cores");
        SendThisVC("(get-unsat-core)");
        var resp = Process.GetProverResponse();
        unsatCore = new List<int>();
        if (resp.Name != "") unsatCore.Add(nameToAssumption[resp.Name]);
        foreach (var s in resp.Arguments) unsatCore.Add(nameToAssumption[s.Name]);

        FlushLogFile();
        Pop();
        return outcome;
    }

    public override void Push()
    {
        SendThisVC("(push 1)");
        DeclCollector.Push();
    }

    public override Outcome CheckAssumptions(List<VCExpr> hardAssumptions, List<VCExpr> softAssumptions, out List<int> unsatisfiedSoftAssumptions, ErrorHandler handler) {
      unsatisfiedSoftAssumptions = new List<int>();

      // First, convert both hard and soft assumptions to SMTLIB strings
      List<string> hardAssumptionStrings = new List<string>();
      foreach (var a in hardAssumptions) {
        hardAssumptionStrings.Add(VCExpr2String(a, 1));
      }
      List<string> currAssumptionStrings = new List<string>();
      foreach (var a in softAssumptions) {
        currAssumptionStrings.Add(VCExpr2String(a, 1));
      }

      Push();
      AssertAxioms();
      foreach (var a in hardAssumptionStrings) {
        SendThisVC("(assert " + a + ")");
      }
      Check();
      Outcome outcome = GetResponse();
      if (outcome != Outcome.Invalid) {
        Pop();
        return outcome;
      }

      int k = 0;
      List<string> relaxVars = new List<string>();
      while (true) {
        Push();
        foreach (var a in currAssumptionStrings) {
          SendThisVC("(assert " + a + ")");
        }
        Check();
        outcome = CheckOutcomeCore(handler);
        if (outcome != Outcome.Valid)
          break;
        Pop();
        string relaxVar = "relax_" + k;
        relaxVars.Add(relaxVar);
        SendThisVC("(declare-fun " + relaxVar + " () Int)");
        List<string> nextAssumptionStrings = new List<string>();
        for (int i = 0; i < currAssumptionStrings.Count; i++) {
          string constraint = "(= " + relaxVar + " " + i + ")";
          nextAssumptionStrings.Add("(or " + currAssumptionStrings[i] + " " + constraint + ")");
        }
        currAssumptionStrings = nextAssumptionStrings;
        k++;
      }

      if (outcome == Outcome.Invalid) {
        foreach (var relaxVar in relaxVars) {
          SendThisVC("(get-value (" + relaxVar + "))");
          FlushLogFile();
          var resp = Process.GetProverResponse();
          if (resp == null) break;
          if (!(resp.Name == "" && resp.ArgCount == 1)) break;
          resp = resp.Arguments[0];
          if (!(resp.Name != "" && resp.ArgCount == 1)) break;
          resp = resp.Arguments[0]; 
          if (resp.ArgCount != 0)
            break;
          int v;
          if (int.TryParse(resp.Name, out v))
            unsatisfiedSoftAssumptions.Add(v);
          else
            break;
        }
        Pop();
      }

      Pop();
      return outcome;
    }
  }

  public class SMTLibProverContext : DeclFreeProverContext
  {
    internal SMTLibProcessTheoremProver parent;

    public readonly Dictionary<CtorType, List<Function>> KnownDatatypeConstructors = new Dictionary<CtorType, List<Function>>();

    public SMTLibProverContext(VCExpressionGenerator gen,
                               VCGenerationOptions genOptions)
      : base(gen, genOptions)
    {
    }

    protected SMTLibProverContext(SMTLibProverContext par)
      : base(par)
    {
    }

    public override object Clone()
    {
      return new SMTLibProverContext(this);
    }

    public override string Lookup(VCExprVar var)
    {
      VCExprVar v = parent.AxBuilder.TryTyped2Untyped(var);
      if (v != null) {
        var = v;
      }
      return parent.Namer.Lookup(var);
    }

    public override void DeclareFunction(Function f, string attributes) {
      if (f is DatatypeConstructor) {
        CtorType datatype = (CtorType) f.OutParams[0].TypedIdent.Type;
        if (!KnownDatatypeConstructors.ContainsKey(datatype))
          KnownDatatypeConstructors[datatype] = new List<Function>();
        KnownDatatypeConstructors[datatype].Add(f);
      }
      base.DeclareFunction(f, attributes);
    }
  }

  public class Factory : ProverFactory
  {
    public override object SpawnProver(ProverOptions options, object ctxt)
    {
      //Contract.Requires(ctxt != null);
      //Contract.Requires(options != null);
      Contract.Ensures(Contract.Result<object>() != null);

      return this.SpawnProver(options,
                              cce.NonNull((SMTLibProverContext)ctxt).ExprGen,
                              cce.NonNull((SMTLibProverContext)ctxt));
    }

    public override object NewProverContext(ProverOptions options)
    {
      //Contract.Requires(options != null);
      Contract.Ensures(Contract.Result<object>() != null);

      VCExpressionGenerator gen = new VCExpressionGenerator();
      List<string>/*!>!*/ proverCommands = new List<string/*!*/>();
      proverCommands.Add("smtlib");
      var opts = (SMTLibProverOptions)options ;
      if (opts.Solver == SolverKind.Z3)
        proverCommands.Add("z3");
      else
        proverCommands.Add("external");
      VCGenerationOptions genOptions = new VCGenerationOptions(proverCommands);
      return new SMTLibProverContext(gen, genOptions);
    }

    public override ProverOptions BlankProverOptions()
    {
      return new SMTLibProverOptions();
    }

    protected virtual SMTLibProcessTheoremProver SpawnProver(ProverOptions options,
                                                              VCExpressionGenerator gen,
                                                              SMTLibProverContext ctx)
    {
      Contract.Requires(options != null);
      Contract.Requires(gen != null);
      Contract.Requires(ctx != null);
      Contract.Ensures(Contract.Result<SMTLibProcessTheoremProver>() != null);

      return new SMTLibProcessTheoremProver(options, gen, ctx);
    }

    public override bool SupportsLabels(ProverOptions options)
    {
      return ((SMTLibProverOptions)options).SupportsLabels;
    }
  }
}
