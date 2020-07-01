using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HotChocolate.Language;
using HotChocolate.Types;

namespace HotChocolate.Execution.Utilities
{
    internal sealed partial class ResolverTask : ITask
    {
        private readonly MiddlewareContext _context = new MiddlewareContext();
        private ValueTask _task;
        private IOperationContext _operationContext = default!;
        private IPreparedSelection _selection = default!;

        public bool IsCompleted => _task.IsCompleted;

        public void BeginExecute()
        {
            _operationContext.Execution.TaskStats.TaskStarted();
            _task = ExecuteInternalAsync();
        }

        private async ValueTask ExecuteInternalAsync()
        {
            using (_operationContext.DiagnosticEvents.ResolveFieldValue(_context))
            {
                try
                {
                    bool errors = true;

                    if (_selection.Arguments.TryCoerceArguments(
                        _context.Variables, 
                        _context.ReportError, 
                        out IReadOnlyDictionary<NameString, PreparedArgument>? coercedArgs)) 
                    {
                        _context.Arguments = coercedArgs;
                        await ExecuteResolverPipelineAsync().ConfigureAwait(false);
                        errors = false;
                    }

                    if (_context.RequestAborted.IsCancellationRequested)
                    {
                        return;
                    }

                    CompleteValue(withErrors: errors);
                }
                catch (GraphQLException ex)
                {
                    foreach (IError error in ex.Errors)
                    {
                        _context.ReportError(error);
                    }
                    _context.Result = null;
                }
                catch (Exception ex)
                {
                    _context.ReportError(ex);
                    _context.Result = null;
                }
                finally
                {
                    _operationContext.Execution.TaskStats.TaskCompleted();
                    _operationContext.Execution.TaskPool.Return(this);
                }
            }
        }

        private async ValueTask ExecuteResolverPipelineAsync()
        {
            await _context.ResolverPipeline(_context).ConfigureAwait(false);

            switch (_context.Result)
            {
                case IError error:
                    _context.ReportError(error);
                    _context.Result = null;
                    break;

                case IEnumerable<IError> errors:
                    foreach (IError error in errors)
                    {
                        _context.ReportError(error);
                    }
                    _context.Result = null;
                    break;
            }
        }

        private void CompleteValue(bool withErrors)
        {
            object? completedValue = null;

            try
            {
                // we will only try to complete the resolver value if there are no known errors.
                if (!withErrors)
                {
                    if (ValueCompletion.TryComplete(
                        _operationContext,
                        _context,
                        _context.Path,
                        _context.Field.Type,
                        _context.Result,
                        out completedValue) &&
                        !_context.Field.Type.IsLeafType() &&
                        completedValue is IHasResultDataParent result)
                    {
                        result.Parent = _context.ResultMap;
                    }
                }
            }
            catch (Exception ex)
            {
                _context.ReportError(ex);
                _context.Result = null;
            }

            if (completedValue is null && _context.Field.Type.IsNonNullType())
            {
                // if we detect a non-null violation we will stash it for later.
                // the non-null propagation is delayed so that we can parallelize better.
                _operationContext.Result.AddNonNullViolation(
                    _context.FieldSelection,
                    _context.Path,
                    _context.ResultMap);
            }
            else
            {
                _context.ResultMap.SetValue(
                    _context.ResponseIndex,
                    _context.ResponseName,
                    completedValue,
                    _context.Field.Type.IsNullableType());
            }
        }
    }
}