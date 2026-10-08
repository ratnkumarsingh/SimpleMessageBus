-- Returns one row with Allowed = 1 when the application is an active admin, or holds the permission
-- (or Manage) on the resource. Used by every procedure that acts on behalf of a caller.
CREATE OR ALTER FUNCTION broker.fn_HasPermission
(
    @AppId        uniqueidentifier,
    @ResourceType varchar(20),
    @ResourceId   uniqueidentifier,
    @Permission   varchar(20)
)
RETURNS TABLE
AS
RETURN
    SELECT CAST(CASE
        WHEN EXISTS (SELECT 1 FROM broker.Applications a
                     WHERE a.AppId = @AppId AND a.IsActive = 1 AND a.IsAdmin = 1) THEN 1
        WHEN EXISTS (SELECT 1 FROM broker.AppPermissions p
                     JOIN broker.Applications a ON a.AppId = p.AppId AND a.IsActive = 1
                     WHERE p.AppId = @AppId
                       AND p.ResourceType = @ResourceType
                       AND p.ResourceId = @ResourceId
                       AND p.Permission IN (@Permission, 'Manage')) THEN 1
        ELSE 0 END AS bit) AS Allowed;
GO
