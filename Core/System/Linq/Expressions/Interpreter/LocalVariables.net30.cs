#if NET20 || NET30

// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Theraot.Core.System.Runtime.CompilerServices;

namespace Theraot.Core.System.Linq.Expressions.Interpreter
{
    internal sealed class LocalVariable
    {
        private const int _isBoxedFlag = 1;
        private const int _inClosureFlag = 2;

        public readonly int Index;
        private int _flags;

        public bool IsBoxed
        {
            get { return (_flags & _isBoxedFlag) != 0; }

            set
            {
                if (value)
                {
                    _flags |= _isBoxedFlag;
                }
                else
                {
                    _flags &= ~_isBoxedFlag;
                }
            }
        }

        public bool InClosure
        {
            get { return (_flags & _inClosureFlag) != 0; }
        }

        public bool InClosureOrBoxed
        {
            get { return InClosure | IsBoxed; }
        }

        internal LocalVariable(int index, bool closure, bool boxed)
        {
            Index = index;
            _flags = (closure ? _inClosureFlag : 0) | (boxed ? _isBoxedFlag : 0);
        }

        internal Expression LoadFromArray(Expression frameData, Expression closure)
        {
            Expression result = Expression.ArrayAccess(InClosure ? closure : frameData, Expression.Constant(Index));
            return (IsBoxed && !InClosure) ? Expression.Convert(result, typeof(StrongBox<object>)) : result;
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}: {1} {2}", Index, IsBoxed ? "boxed" : null, InClosure ? "in closure" : null);
        }
    }

    internal struct LocalDefinition
    {
        private readonly int _index;
        private readonly ParameterExpression _parameter;

        internal LocalDefinition(int localIndex, ParameterExpression parameter)
        {
            _index = localIndex;
            _parameter = parameter;
        }

        public int Index
        {
            get { return _index; }
        }

        public ParameterExpression Parameter
        {
            get { return _parameter; }
        }

        public override bool Equals(object obj)
        {
            if (obj is LocalDefinition)
            {
                var other = (LocalDefinition)obj;
                return other.Index == Index && other.Parameter == Parameter;
            }

            return false;
        }

        public override int GetHashCode()
        {
            if (_parameter == null)
            {
                return 0;
            }
            return _parameter.GetHashCode() ^ _index.GetHashCode();
        }

        public static bool operator ==(LocalDefinition self, LocalDefinition other)
        {
            return self.Index == other.Index && self.Parameter == other.Parameter;
        }

        public static bool operator !=(LocalDefinition self, LocalDefinition other)
        {
            return self.Index != other.Index || self.Parameter != other.Parameter;
        }
    }

    internal sealed class LocalVariables
    {
        private readonly HybridReferenceDictionary<ParameterExpression, VariableScope> _variables = new HybridReferenceDictionary<ParameterExpression, VariableScope>();
        private Dictionary<ParameterExpression, LocalVariable> _closureVariables;

        private int _localCount, _maxLocalCount;

        public LocalDefinition DefineLocal(ParameterExpression variable, int start)
        {
            var result = new LocalVariable(_localCount++, false, false);
            _maxLocalCount = Math.Max(_localCount, _maxLocalCount);

            VariableScope existing, newScope;
            if (_variables.TryGetValue(variable, out existing))
            {
                newScope = new VariableScope(result, start, existing);
                if (existing.ChildScopes == null)
                {
                    existing.ChildScopes = new List<VariableScope>();
                }
                existing.ChildScopes.Add(newScope);
            }
            else
            {
                newScope = new VariableScope(result, start, null);
            }

            _variables[variable] = newScope;
            return new LocalDefinition(result.Index, variable);
        }

        public void UndefineLocal(LocalDefinition definition, int end)
        {
            var scope = _variables[definition.Parameter];
            scope.Stop = end;
            if (scope.Parent != null)
            {
                _variables[definition.Parameter] = scope.Parent;
            }
            else
            {
                _variables.Remove(definition.Parameter);
            }

            _localCount--;
        }

        internal void Box(ParameterExpression variable, InstructionList instructions)
        {
            var scope = _variables[variable];

            var local = scope.Variable;
            Debug.Assert(!local.IsBoxed && !local.InClosure);
            _variables[variable].Variable.IsBoxed = true;

            var curChild = 0;
            for (var i = scope.Start; i < scope.Stop && i < instructions.Count; i++)
            {
                if (scope.ChildScopes != null && scope.ChildScopes[curChild].Start == i)
                {
                    // skip boxing in the child scope
                    var child = scope.ChildScopes[curChild];
                    i = child.Stop;

                    curChild++;
                    continue;
                }

                instructions.SwitchToBoxed(local.Index, i);
            }
        }

        public int LocalCount
        {
            get { return _maxLocalCount; }
        }

        public int GetOrDefineLocal(ParameterExpression var)
        {
            var index = GetLocalIndex(var);
            if (index == -1)
            {
                return DefineLocal(var, 0).Index;
            }
            return index;
        }

        public int GetLocalIndex(ParameterExpression var)
        {
            VariableScope loc;
            return _variables.TryGetValue(var, out loc) ? loc.Variable.Index : -1;
        }

        public bool TryGetLocalOrClosure(ParameterExpression var, out LocalVariable local)
        {
            VariableScope scope;
            if (_variables.TryGetValue(var, out scope))
            {
                local = scope.Variable;
                return true;
            }
            if (_closureVariables != null && _closureVariables.TryGetValue(var, out local))
            {
                return true;
            }

            local = null;
            return false;
        }

        /// <summary>
        /// Gets a copy of the local variables which are defined in the current scope.
        /// </summary>
        /// <returns></returns>
        internal Dictionary<ParameterExpression, LocalVariable> CopyLocals()
        {
            var res = new Dictionary<ParameterExpression, LocalVariable>(_variables.Count);
            foreach (var keyValue in _variables)
            {
                res[keyValue.Key] = keyValue.Value.Variable;
            }
            return res;
        }

        internal bool ContainsVariable(ParameterExpression variable)
        {
            return _variables.ContainsKey(variable);
        }

        /// <summary>
        /// Gets the variables which are defined in an outer scope and available within the current scope.
        /// </summary>
        internal Dictionary<ParameterExpression, LocalVariable> ClosureVariables
        {
            get { return _closureVariables; }
        }

        internal LocalVariable AddClosureVariable(ParameterExpression variable)
        {
            if (_closureVariables == null)
            {
                _closureVariables = new Dictionary<ParameterExpression, LocalVariable>();
            }
            var result = new LocalVariable(_closureVariables.Count, true, false);
            _closureVariables.Add(variable, result);
            return result;
        }

        /// <summary>
        /// Tracks where a variable is defined and what range of instructions it's used in
        /// </summary>
        private sealed class VariableScope
        {
            public readonly int Start;
            public int Stop = int.MaxValue;
            public readonly LocalVariable Variable;
            public readonly VariableScope Parent;
            public List<VariableScope> ChildScopes;

            public VariableScope(LocalVariable variable, int start, VariableScope parent)
            {
                Variable = variable;
                Start = start;
                Parent = parent;
            }
        }
    }
}

#endif