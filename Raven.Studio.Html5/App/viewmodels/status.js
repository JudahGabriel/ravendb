define(["require", "exports", "plugins/router", "common/raven"], function(require, exports, __router__, __raven__) {
    
    
    
    var router = __router__;

    var raven = __raven__;

    var status = (function () {
        function status() {
            this.displayName = "status";
            this.activeView = ko.observable('');
            this.ravenDb = new raven();
        }
        status.prototype.activate = function (args) {
            console.log(args);
            var view = args && args.view ? args.view : 'statistics';
            this.activeView(view);
        };
        status.prototype.canDeactivate = function () {
            return true;
        };

        status.prototype.switchView = function (view) {
            console.log(view);
            this.activeView(view);
            router.navigate("#status?view=" + view, false);
        };
        return status;
    })();

    
    return status;
});
//# sourceMappingURL=status.js.map
