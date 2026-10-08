Allocated AS (
    SELECT i.Tag, i.VoucherNo, i.AccountId, i.DueDate, i.PlanAmount,
           CASE WHEN COALESCE(p.PaidAmount, 0) <= i.CumBefore THEN 0
                WHEN COALESCE(p.PaidAmount, 0) - i.CumBefore >= i.PlanAmount THEN i.PlanAmount
                ELSE COALESCE(p.PaidAmount, 0) - i.CumBefore END AS AllocatedAmount
    FROM (SELECT Tag, VoucherNo, AccountId, DueDate, PlanAmount,
                 COALESCE(SUM(PlanAmount) OVER (PARTITION BY Tag ORDER BY DueDate, VoucherNo, AccountId
                          ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS CumBefore
          FROM Instalment) i
    LEFT JOIN TagPaid p ON p.Tag = i.Tag
)
