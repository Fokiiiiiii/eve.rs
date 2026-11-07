ALTER TABLE objectives
    ADD CONSTRAINT objectives_target_finite_positive
        CHECK (target > 0 AND target < 'Infinity'::double precision),
    ADD CONSTRAINT objectives_progress_finite_nonnegative
        CHECK (progress >= 0 AND progress < 'Infinity'::double precision);
