define(["require", "exports"], function(require, exports) {
    var status = (function () {
        function status() {
            this.displayName = "status";
        }
        status.prototype.activate = function (args) {
        };

        status.prototype.canDeactivate = function () {
            return true;
        };
        return status;
    })();

    
    return status;
});
//# sourceMappingURL=status.js.map
