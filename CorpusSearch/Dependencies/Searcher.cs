﻿using CorpusSearch.Dependencies.csly;
using CorpusSearch.Dependencies.Lucene;
using CorpusSearch.Model;
using Lucene.Net.Index;
using Lucene.Net.Search.Spans;
using Lucene.Net.Util;
using sly.parser;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CorpusSearch.Dependencies
{
    public class Searcher
    {
        // responsible for converting from a 
        private readonly LuceneIndex luceneSearch;
        private readonly SearchParser parser;

        public Searcher(LuceneIndex luceneIndex, SearchParser parser)
        {
            this.luceneSearch = luceneIndex;
            this.parser = parser;
        }

        internal SearchResult SearchWork(string ident, string query, SearchOptions options)
        {
            // HACK: use the ScanOptions as they're the same for now
            var scanOptionsHack = new ScanOptions { SearchType = options.Type };

            // parse the string into a Result<Expression>
            var parsed = parser.Parse(query);

            // Convert the result to a Lucene Span Query (or throw ArgumentException)
            SpanQuery spanQuery = ToSpanQuery(parsed, scanOptionsHack);

            return luceneSearch.Search(ident, spanQuery);

        }

        /// <summary>
        /// Returns all lines for a provided document
        /// </summary>
        /// <param name="ident">The ID of the document</param>
        internal List<DocumentLine> GetAllLines(string ident)
        {
            return luceneSearch.GetAllLines(ident);
        }

        public ScanResult Scan(string query)
        {
            return Scan(query, ScanOptions.Default);
        }

        public ScanResult Scan(string query, ScanOptions searchOptions)
        {
            // parse the string into a Result<Expression>
            var parsed = parser.Parse(query);

            // Convert the result to a Lucene Span Query (or throw ArgumentException)
            SpanQuery spanQuery = ToSpanQuery(parsed, searchOptions);

            // Perform the search
            return luceneSearch.Scan(spanQuery);
        }


        private SpanQuery ToSpanQuery(ParseResult<ExpressionToken, Expression> parsed, ScanOptions searchOptions)
        {
            if (!parsed.IsOk || parsed.Result == null)
            {
                throw new ArgumentException("failed to parse query: " + string.Join(",", parsed.Errors.Select(x => x.ErrorMessage)));
            }

            return ToSpanQuery(parsed.Result, searchOptions);
        }



        private SpanQuery ToSpanQuery(Expression result, ScanOptions searchOptions)
        {
            SpanQuery ToSpanQueryInner(Expression res) => ToSpanQuery(res, searchOptions);
            SpanQuery ToManx(string value) => ManxTermQuery(value, searchOptions);

            switch (result)
            {
                case StringExpression s:
                    return ToManx(s.Term);
                case OrExpression or:
                    {
                        var left = ToSpanQueryInner(or.Left);
                        var right = ToSpanQueryInner(or.Right);
                        return new SpanOrQuery(left, right);
                    }
                case AndExpression and:
                    {
                        var left = ToSpanQueryInner(and.Left);
                        var right = ToSpanQueryInner(and.Right);
                        // TODO: Might be something better than an infinite slop
                        return new SpanNearQuery(new[] { left, right }, int.MaxValue, false);
                    }
                case AdjacentWordExpression e:
                    var queries = e.Words.Select(ToManx);
                    return new SpanNearQuery(queries.ToArray(), 0, true);
                case NotExpression e:
                    // This also feels inefficient: we want a "not" for the document, and the easiest way seems to be a "not near the span"
                    SpanQuery l = ToSpanQueryInner(e.Left);
                    SpanQuery r = ToSpanQueryInner(e.Right);
                    var notNear = new SpanNearQuery(new[] { l, r }, int.MaxValue, false);
                    return new SpanNotQuery(l, notNear);
                case WrappedExpression w:
                    return ToSpanQueryInner(w.Wrapped);
                default:
                    throw new NotImplementedException(result.GetType().ToString());
            }
        }

        private static SpanQuery ManxTermQuery(string value, ScanOptions searchOptions)
        {
            Term term = new Term(GetTermKey(searchOptions), GetTerm(value, searchOptions));
            if (searchOptions.NormalizeDiacritics)
            {
                var manx = new ManxQuery(term);
                return new SpanMultiTermQueryWrapper<ManxQuery>(manx);
            }
            else if (ExtendedWildcardQuery.AppliesTo(value))
            {
                return new SpanMultiTermQueryWrapper<ExtendedWildcardQuery>(new ExtendedWildcardQuery(term)); 
            }
            else
            {
                // TODO: This does not handle trailing question marks and some forms of dashes
                return new SpanTermQuery(term);
            }
        }

        private static string GetTerm(string value, ScanOptions searchOptions)
        {
            switch (searchOptions.SearchType)
            {
                case SearchType.Manx: return DocumentLine.NormalizeManx(value, allowQuestionMark: true);
                case SearchType.English: return DocumentLine.NormalizeEnglish(value, allowQuestionMark: true);
                default: throw new ArgumentException("Unhandlded case: " + searchOptions.SearchType);
            }
        }

        private static string GetTermKey(ScanOptions searchOptions)
        {
            switch (searchOptions.SearchType)
            {
                case SearchType.Manx: return LuceneIndex.DOCUMENT_NORMALIZED_MANX;
                case SearchType.English: return LuceneIndex.DOCUMENT_NORMALIZED_ENGLISH;
                default: throw new ArgumentException("Unhandlded case: " + searchOptions.SearchType);
            }
        }

        internal void OnAllDocumentsAdded()
        {
            luceneSearch.Compact();
        }

        internal void AddDocument(IDocument document, IEnumerable<DocumentLine> data)
        {
            this.luceneSearch.Add(document, data);
        }
    }
}
