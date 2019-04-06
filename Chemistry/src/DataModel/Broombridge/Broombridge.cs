﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Linq;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Microsoft.Quantum.Chemistry
{
    /// <summary>
    /// Functionality to validate and parse Broombridge
    /// </summary>
    public static partial class Broombridge
    {
        /// <summary>
        /// Alias for Broombridge latest supported format.
        /// </summary>
        public class Current : V0_2
        {

        }

        /// <summary>
        /// Class containing Broombridge version number management.
        /// </summary>
        public static class Version
        {
            /// <summary>
            /// Enumerable item for Broombridge version numbers.
            /// </summary>
            public enum Number
            {
                v0_1 = 0, v0_2 = 1
            }

            /// <summary>
            /// Dictionary from version number strings to version number types.
            /// </summary>
            public static Dictionary<string, Number> VersionNumberDict = new Dictionary<string, Number>()
            {
                {"0.1", Number.v0_1 },
                {"0.2", Number.v0_2 }
            };

            /// <summary>
            /// Parse version number string
            /// </summary>
            /// <param name="versionNumber">Version number string</param>
            /// <returns>Version number in enum `Number` format.</returns>
            public static Number ParseVersionNumber(string versionNumber)
            {
                return VersionNumberDict[versionNumber];
            }
        }
        
    }
    // Parts of this might be merged intro Broombridge parsing due to overlap.


    /// <summary>
    /// Broombridge with type information parsed, and only relevant metadata kept.
    /// </summary>
    public struct BroombridgeTyped { 
 
        public int NOrbitals, NElectrons;

        public double IdentityTerm;
        public HashSet<OrbitalIntegral> OneBodyTerms;
        public HashSet<OrbitalIntegral> TwoBodyTerms;
        public Dictionary<string, FermionHamiltonian.InputState> InitialStates;

        public class Config : FermionHamiltonian.Config { }
        public readonly SpinOrbital.Config.IndexConvention.Type IndexConvention;

        /// <summary>
        /// Extracts only the required information from a Broombridge problem instance.
        /// </summary>
        /// <param name="broombridgeProblem">A Broombridge problem description.</param>
        /// <param name="indexConvention">The indexing convention used to map a spin-orbital indicx to a single integer.</param>
        public BroombridgeTyped(Broombridge.Current.ProblemDescription broombridgeProblem, SpinOrbital.Config.IndexConvention.Type indexConvention = SpinOrbital.Config.IndexConvention.Default)
        {
            IndexConvention = indexConvention;
            NOrbitals = broombridgeProblem.NOrbitals;
            NElectrons = broombridgeProblem.NElectrons;

            IdentityTerm = broombridgeProblem.CoulombRepulsion.Value + broombridgeProblem.EnergyOffset.Value;

            // This will convert from Broombridge 1-indexing to 0-indexing.
            OneBodyTerms = new HashSet<OrbitalIntegral> (broombridgeProblem.Hamiltonian.OneElectronIntegrals.Values.Select(
                o => new OrbitalIntegral(o.Item1.Select(k => k - 1), o.Item2, OrbitalIntegral.Convention.Mulliken).ToCanonicalForm())
                .Distinct().ToList());

            // This will convert from Broombridge 1-indexing to 0-indexing.
            // This will convert to Dirac-indexing.
            TwoBodyTerms = new HashSet<OrbitalIntegral> (broombridgeProblem.Hamiltonian.TwoElectronIntegrals.Values.Select(
                o => new OrbitalIntegral(o.Item1.Select(k => k - 1), o.Item2, OrbitalIntegral.Convention.Mulliken)).ToList());

            InitialStates = broombridgeProblem.InitialStates.ToDictionary(
                o => o.Label,
                o => ParseInitialState(o,indexConvention)
                );                
        }

        internal static FermionHamiltonian.InputState ParseInitialState(Broombridge.V0_2.State initialState, SpinOrbital.Config.IndexConvention.Type indexConvention)
        {
            var state = new FermionHamiltonian.InputState();
            state.type = ParseInitialStateMethod(initialState.Method);
            state.Label = initialState.Label;
            if (state.type == FermionHamiltonian.StateType.Sparse_Multi_Configurational)
            {
                state.Superposition = ParseInputState(initialState.Superposition, indexConvention);
            }
            else if (state.type == FermionHamiltonian.StateType.Unitary_Coupled_Cluster)
            {
                var referenceState = ParseInputState(initialState.ClusterOperator.Reference.Select(o => o.ToString()).ToList(), indexConvention);
                var oneBodyTerms = initialState.ClusterOperator.OneBodyAmplitudes.Select(o => ParseUnitaryCoupledClisterInputState(o, indexConvention));
                var twoBodyTerms = initialState.ClusterOperator.TwoBodyAmplitudes.Select(o => ParseUnitaryCoupledClisterInputState(o, indexConvention));
                var clusterTerms = oneBodyTerms.Concat(twoBodyTerms).ToList();
                // The last term is the reference state.
                clusterTerms.Add(referenceState);

                state.Superposition = clusterTerms.ToArray() ;
            }
            else
            {
                throw new System.ArgumentException($"initial state `{state.Label}` is not recognized or implemented.");
            }
            return state;
        }

        internal static FermionHamiltonian.StateType ParseInitialStateMethod(string state)
        {
            if (state.ToLowerInvariant() == "single_configurational")
            {
                return FermionHamiltonian.StateType.Single_Configurational;
            }
            else if(state.ToLowerInvariant() == "sparse_multi_configurational")
            {
                return FermionHamiltonian.StateType.Sparse_Multi_Configurational;
            }
            else if(state.ToLowerInvariant() == "unitary_coupled_cluster")
            {
                return FermionHamiltonian.StateType.Unitary_Coupled_Cluster;
            }
            else
            {
                return FermionHamiltonian.StateType.Default;
            }
        }

        internal static ((Double, Double), FermionTerm)[] ParseInputState(List<List<object>> superposition, SpinOrbital.Config.IndexConvention.Type indexConvention)
        {
            return superposition.Select(
                o => ParseInputState(   
                        o.Select(k => k.ToString()).ToList(), indexConvention
                        )
                    ).ToArray();
        }

        public static ((Double, Double), FermionTerm) ParseInputState(List<string> superpositionElement, SpinOrbital.Config.IndexConvention.Type indexConvention)
        {
            var amplitude = Double.Parse(superpositionElement.First(), System.Globalization.CultureInfo.InvariantCulture);
            var initialState = superpositionElement.Last();
            var ca = new List<Int64>();
            var so = new List<SpinOrbital>();

            for (int i = 1; i < superpositionElement.Count() - 1; i++)
            {
                FermionTerm singleTerm = ParsePolishNotation(superpositionElement[i], indexConvention);
                ca.Add(singleTerm.CreationAnnihilationIndices.First());
                so.Add(singleTerm.SpinOrbitalIndices.First());
            }
            FermionTerm term = new FermionTerm(ca.ToArray(), so.ToArray(), amplitude);

            var canonicalOrder = term.ToCanonicalOrder();
            var noAnnihilationTerm = canonicalOrder.Where(o => !(o.CreationAnnihilationIndices.Contains(0)));
            var finalAmplitude = 0.0;
            // check if there are any valid terms.
            if (noAnnihilationTerm.Count() == 1)
            {
                term = noAnnihilationTerm.Single();
                finalAmplitude = term.coeff;
            }
            term.coeff = 1.0;
            return ((finalAmplitude, 0.0), term);
        }

        public static ((Double, Double), FermionTerm) ParseUnitaryCoupledClisterInputState(List<string> clusterTerm, SpinOrbital.Config.IndexConvention.Type indexConvention)
        {
            var amplitude = Double.Parse(clusterTerm.First(), System.Globalization.CultureInfo.InvariantCulture);
            var ca = new List<Int64>();
            var so = new List<SpinOrbital>();

            for (int i = 1; i < clusterTerm.Count(); i++)
            {
                FermionTerm singleTerm = ParsePolishNotation(clusterTerm[i], indexConvention);
                ca.Add(singleTerm.CreationAnnihilationIndices.First());
                so.Add(singleTerm.SpinOrbitalIndices.First());
            }
            FermionTerm term = new FermionTerm(ca.ToArray(), so.ToArray(), amplitude);

            return ((term.coeff, 0), term);
        }

        internal static FermionTerm ParsePolishNotation(string input, SpinOrbital.Config.IndexConvention.Type indexConvention)
        {
            // Regex match examples: (1a)+ (2a)+ (3a)+ (4a)+ (5a)+ (6a)+ (1b)+ (2b)- (3b)+
            Regex regex = new Regex(@"(\((?<orbital>\d+)(?<spin>[ab])\)(?<operator>\+*))");
            Match match = regex.Match(input);
            if (match.Success)
            {
                // Convert from Broombridge 1-indexing to 0-indexing.
                var orbital = Int64.Parse(match.Groups["orbital"].ToString()) - 1;
                var spin = match.Groups["spin"].ToString() == "a" ? Spin.u : Spin.d;
                var conjugate = match.Groups["operator"].ToString() == "+" ? 1 : 0;
                return new FermionTerm(new long[] { conjugate }, new SpinOrbital[] { new SpinOrbital(orbital, spin, indexConvention) }, 1.0);
            }
            else
            {
                throw new System.ArgumentException($"{input} is not valid Polish notation");
            }
        }
    }
    

  
}