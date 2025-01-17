﻿using DafnyServer.CounterExampleGeneration;
using Microsoft.Boogie;
using Microsoft.Dafny.LanguageServer.Language;
using Microsoft.Dafny.LanguageServer.Workspace;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Dafny.LanguageServer.Handlers.Custom {
  public class DafnyCounterExampleHandler : ICounterExampleHandler {
    private readonly ILogger logger;
    private readonly IDocumentDatabase documents;

    public DafnyCounterExampleHandler(ILogger<DafnyCounterExampleHandler> logger, IDocumentDatabase documents) {
      this.logger = logger;
      this.documents = documents;
    }

    public async Task<CounterExampleList> Handle(CounterExampleParams request, CancellationToken cancellationToken) {
      var document = await documents.GetDocumentAsync(request.TextDocument);
      if (document == null) {
        logger.LogWarning("counter-examples requested for unloaded document {DocumentUri}", request.TextDocument.Uri);
        return new CounterExampleList();
      }
      return new CounterExampleLoader(logger, document, request.CounterExampleDepth, cancellationToken).GetCounterExamples();
    }

    private class CounterExampleLoader {
      private const string InitialStateName = "<initial>";
      private static readonly Regex StatePositionRegex = new(
        @".*\.dfy\((?<line>\d+),(?<character>\d+)\)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline
      );

      private readonly ILogger logger;
      private readonly DafnyDocument document;
      private readonly CancellationToken cancellationToken;
      private readonly int counterExampleDepth;

      public CounterExampleLoader(ILogger logger, DafnyDocument document, int counterExampleDepth, CancellationToken cancellationToken) {
        this.logger = logger;
        this.document = document;
        this.cancellationToken = cancellationToken;
        this.counterExampleDepth = counterExampleDepth;
      }

      public CounterExampleList GetCounterExamples() {
        if (document.SerializedCounterExamples == null) {
          logger.LogDebug("got no counter-examples for document {DocumentUri}", document.Uri);
          return new CounterExampleList();
        }
        var counterExamples = GetLanguageSpecificModels(document.SerializedCounterExamples)
          .SelectMany(GetCounterExamples)
          .ToArray();
        return new CounterExampleList(counterExamples);
      }

      private IEnumerable<DafnyModel> GetLanguageSpecificModels(string serializedCounterExamples) {
        using var counterExampleReader = new StringReader(serializedCounterExamples);
        return Model.ParseModels(counterExampleReader)
          .WithCancellation(cancellationToken)
          .Select(GetLanguagSpecificModel);
      }

      private DafnyModel GetLanguagSpecificModel(Model model) {
        return new(model);
      }

      private IEnumerable<CounterExampleItem> GetCounterExamples(DafnyModel model) {
        return model.States
          .WithCancellation(cancellationToken)
          .OfType<DafnyModelState>()
          .Where(state => !IsInitialState(state))
          .Select(GetCounterExample);
      }

      private static bool IsInitialState(DafnyModelState state) {
        return state.ShortenedStateName.Equals(InitialStateName);
      }

      private CounterExampleItem GetCounterExample(DafnyModelState state) {
        return new(
          GetPositionFromInitialState(state),
          GetVariablesFromState(state, counterExampleDepth)
        );
      }

      private static Position GetPositionFromInitialState(DafnyModelState state) {
        var match = StatePositionRegex.Match(state.ShortenedStateName);
        if (!match.Success) {
          throw new ArgumentException($"state does not contain position: {state.ShortenedStateName}");
        }
        // Note: lines in a model start with 1, characters/columns with 0.
        return new Position(
          int.Parse(match.Groups["line"].Value) - 1,
          int.Parse(match.Groups["character"].Value)
        );
      }

      private IDictionary<string, string> GetVariablesFromState(DafnyModelState state, int maxDepth) {
        HashSet<DafnyModelVariable> vars = state.ExpandedVariableSet(maxDepth);
        return vars.WithCancellation(cancellationToken).ToDictionary(
            variable => variable.ShortName + ":" + variable.Type.InDafnyFormat(),
            variable => variable.Value
          );
      }
    }
  }
}
