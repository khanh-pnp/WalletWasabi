using System.Linq;

namespace WalletWasabi.Blockchain.TransactionBuilding.BnB;

public class LesserSelectionStrategy : ISearchStrategy
{
	private long _currentInputCosts = 0;
	private long _bestTargetSoFar = long.MinValue;
	private long[]? _bestSelectionSoFar;

	public LesserSelectionStrategy(long target, long[] inputValues, long[] inputCosts)
	{
		InputCosts = inputCosts;
		InputValues = inputValues;
		Target = target;
	}

	/// <summary>Costs corresponding to <see cref="InputValues"/> values.</summary>
	public long[] InputCosts { get; }

	/// <summary>Target value we want to, ideally, sum up from the input values.</summary>
	public long Target { get; }

	/// <summary>Input values sorted in descending orders.</summary>
	public long[] InputValues { get; }

	/// <summary>Gives highest found value selection whose sum is smaller than or equal to <see cref="Target"/>.</summary>
	public long[]? GetBestSelectionFound() => _bestSelectionSoFar?.Where(x => x > 0).ToArray();

	public long ProcessAction(NextAction action, long[] selection, int depth, long oldSum)
	{
		long newSum;

		if (action == NextAction.IncludeFirstThenOmit || action == NextAction.Include)
		{
			if (selection[depth] == 0)
			{
				_currentInputCosts += InputCosts[depth];
			}

			selection[depth] = InputValues[depth];
			newSum = oldSum + selection[depth];
		}
		else
		{
			if (selection[depth] > 0)
			{
				_currentInputCosts -= InputCosts[depth];
			}

			newSum = oldSum - selection[depth];
			selection[depth] = 0;
		}

		return newSum;
	}

	public EvaluationResult Evaluate(long[] selection, int depth, long sum)
	{
		long totalCost = sum + _currentInputCosts;

		if (totalCost > Target)
		{
			// Excessive funds, cut the branch.
			return EvaluationResult.SkipBranch;
		}

		if (depth == selection.Length)
		{
			// Leaf reached, no match
			return EvaluationResult.SkipBranch;
		}

		if (totalCost <= Target)
		{
			if (_bestTargetSoFar < totalCost)
			{
				_bestSelectionSoFar = selection[0..depth];
				_bestTargetSoFar = totalCost;
			}
		}

		return EvaluationResult.Continue;
	}
}
